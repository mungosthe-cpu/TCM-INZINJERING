using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    public const string VolumeDisagreementLayer = "TCM_ZAP_NESLAGANJE";

    /// <summary>
    /// Panel poverenja zapremine: TIN–TIN + Grid + sekcije između dva imenovana terena.
    /// </summary>
    [CommandMethod("TCMTERZAP", CommandFlags.Modal)]
    public void RunTerrainVolumeConfidence()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        try
        {
            var self = TerrainVolumeEngine.RunSelfCheck();
            ed.WriteMessage($"\nTCM-ROADS self-check zapremine: {self}");

            List<string> names;
            string? active;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                names = NamedTerrainSurfaceStore.ListNames(tr, db)
                    .Where(n => !NamedTerrainSurfaceStore.IsBoundaryCompanionName(n))
                    .ToList();
                active = NamedTerrainSurfaceStore.GetActiveName(tr, db);
                tr.Commit();
            }

            if (names.Count < 1)
            {
                ed.WriteMessage(
                    "\nTCM-ROADS: Nema imenovanih terena. Sacuvajte teren (3DFACE teren / Snimi u projekat).");
                return;
            }

            if (names.Count < 2)
            {
                ed.WriteMessage(
                    "\nTCM-ROADS: Potrebna su najmanje 2 imenovana terena (baza + poredjenje).");
            }

#if !BRICSCAD
            var dialog = new TerrainVolumeConfidenceDialog(
                names,
                active,
                names.FirstOrDefault(n =>
                    !string.Equals(n, active, StringComparison.OrdinalIgnoreCase)),
                calculate: (baseName, cmpName, grid, sections, swell, shrink, pickBound) =>
                    CalculateVolume(ed, db, baseName, cmpName, grid, sections, swell, shrink, pickBound),
                showMap: result => DrawDisagreementMap(ed, db, result),
                saveReport: result => SaveVolumeReport(result, db.OriginalFileName));

            AcApp.ShowModalWindow(dialog);
#else
            ed.WriteMessage("\nTCM-ROADS: Panel zapremine zahteva WPF (AutoCAD).");
#endif
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static TerrainVolumeResult? CalculateVolume(
        Editor ed,
        Database db,
        string baseName,
        string cmpName,
        double gridStep,
        int sectionCount,
        double swell,
        double shrink,
        bool pickBoundary)
    {
        IReadOnlyList<Point3d>? basePts;
        IReadOnlyList<Point3d>? cmpPts;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            basePts = NamedTerrainSurfaceStore.TryLoadSurface(tr, db, baseName);
            cmpPts = NamedTerrainSurfaceStore.TryLoadSurface(tr, db, cmpName);
            tr.Commit();
        }

        if (basePts is null || basePts.Count < 3)
        {
            ed.WriteMessage($"\nTCM-ROADS: Teren „{baseName}“ nema tacke.");
            return null;
        }

        if (cmpPts is null || cmpPts.Count < 3)
        {
            ed.WriteMessage($"\nTCM-ROADS: Teren „{cmpName}“ nema tacke.");
            return null;
        }

        IReadOnlyList<Point2d>? ring = null;
        if (pickBoundary)
        {
            ring = PromptClosedBoundary(ed, db);
            if (ring is null)
            {
                ed.WriteMessage("\nTCM-ROADS: Granica nije izabrana — koristi se overlap.");
            }
        }

        var result = TerrainVolumeEngine.Compute(
            baseName, cmpName, basePts, cmpPts,
            new TerrainVolumeEngine.Options
            {
                GridStep = gridStep,
                SectionCount = sectionCount,
                SwellFactor = swell,
                ShrinkFactor = shrink,
                InclusionRing = ring
            });

        ed.WriteMessage(
            $"\nTCM-ROADS zapremina TIN–TIN: iskop={result.Tin.CutVolume:0.000} m³, " +
            $"nasip={result.Tin.FillVolume:0.000} m³, neto={result.Tin.NetVolume:0.000} m³ " +
            $"| poverenje={result.ConfidenceLevel}");
        return result;
    }

    private static IReadOnlyList<Point2d>? PromptClosedBoundary(Editor ed, Database db)
    {
        var peo = new PromptEntityOptions("\nIzaberite zatvorenu poliliniju granice AOI: ")
        {
            AllowNone = true
        };
        peo.SetRejectMessage("\nPotrebna je LWPOLYLINE/POLYLINE.");
        peo.AddAllowedClass(typeof(Polyline), exactMatch: false);
        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
        {
            return null;
        }

        using var tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(per.ObjectId, OpenMode.ForRead) is not Curve curve || curve.IsErased)
        {
            tr.Commit();
            return null;
        }

        var pts = new List<Point2d>();
        if (curve is Polyline pl)
        {
            for (var i = 0; i < pl.NumberOfVertices; i++)
            {
                var p = pl.GetPoint2dAt(i);
                pts.Add(p);
            }

            if (!pl.Closed && pts.Count >= 2)
            {
                var a = pts[0];
                var b = pts[^1];
                if (Math.Abs(a.X - b.X) > 1e-6 || Math.Abs(a.Y - b.Y) > 1e-6)
                {
                    pts.Add(a);
                }
            }
        }
        else
        {
            try
            {
                var start = curve.StartPoint;
                var end = curve.EndPoint;
                const int n = 64;
                for (var i = 0; i <= n; i++)
                {
                    var t = i / (double)n;
                    var p = curve.GetPointAtParameter(
                        curve.StartParam + t * (curve.EndParam - curve.StartParam));
                    pts.Add(new Point2d(p.X, p.Y));
                }

                if (start.DistanceTo(end) > 1e-4)
                {
                    pts.Add(new Point2d(start.X, start.Y));
                }
            }
            catch
            {
                tr.Commit();
                return null;
            }
        }

        tr.Commit();
        return pts.Count >= 3 ? pts : null;
    }

    private static void DrawDisagreementMap(Editor ed, Database db, TerrainVolumeResult result)
    {
        if (result.DisagreementCells.Count == 0)
        {
            ed.WriteMessage("\nTCM-ROADS: Nema celija za mapu neslaganja.");
            return;
        }

        var maxDiff = result.DisagreementCells.Max(c => c.AbsoluteDiff);
        if (maxDiff < 1e-9)
        {
            maxDiff = 1.0;
        }

        // Poziva se iz modalnog dijaloga (dijalog je samo sakriven) — dokument
        // tada nije zakljucan, pa upis bez LockDocument baca eLockViolation.
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        using var docLock = doc?.LockDocument();
        using var tr = db.TransactionManager.StartTransaction();
        EnsureVolumeLayer(tr, db);
        EraseVolumeMap(tr, db);

        var ms = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

        var drawn = 0;
        foreach (var cell in result.DisagreementCells)
        {
            // Crta samo znacajne razlike (top ~ vizuelni prag)
            if (cell.AbsoluteDiff < maxDiff * 0.05 && cell.AbsoluteDiff < 0.01)
            {
                continue;
            }

            var t = Math.Max(0, Math.Min(1, cell.AbsoluteDiff / maxDiff));
            var aci = DisagreementToAci(t);
            var z = 0.0;
            var face = new Face(
                new Point3d(cell.MinX, cell.MinY, z),
                new Point3d(cell.MaxX, cell.MinY, z),
                new Point3d(cell.MaxX, cell.MaxY, z),
                new Point3d(cell.MinX, cell.MaxY, z),
                true, true, true, true)
            {
                Layer = VolumeDisagreementLayer,
                Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci)
            };
            ms.AppendEntity(face);
            tr.AddNewlyCreatedDBObject(face, true);
            drawn++;
        }

        // Legenda (tekst)
        var legend = new DBText
        {
            Position = new Point3d(result.MinX, result.MaxY + Math.Max(2.0, result.GridStep), 0),
            Height = Math.Max(0.5, result.GridStep * 0.35),
            TextString = $"TCM mapa neslaganja Grid vs TIN  |  max dV={maxDiff:0.000} m3/celija  |  " +
                   $"poverenje {result.ConfidenceLevel}",
            Layer = VolumeDisagreementLayer,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, 7)
        };
        ms.AppendEntity(legend);
        tr.AddNewlyCreatedDBObject(legend, true);

        tr.Commit();
        ed.WriteMessage(
            $"\nTCM-ROADS: Mapa neslaganja — {drawn} celija na lejeru {VolumeDisagreementLayer}.");
        ed.Regen();
    }

    private static short DisagreementToAci(double t) =>
        t switch
        {
            < 0.2 => 3,   // zeleno
            < 0.4 => 2,   // žuto
            < 0.6 => 30,  // narandžasto
            < 0.8 => 1,   // crveno
            _ => 6        // magenta — najgore
        };

    private static void EnsureVolumeLayer(Transaction tr, Database db)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(VolumeDisagreementLayer))
        {
            return;
        }

        lt.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = VolumeDisagreementLayer,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, 30)
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static void EraseVolumeMap(Transaction tr, Database db)
    {
        var ms = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        var doomed = new List<ObjectId>();
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent || ent.IsErased)
            {
                continue;
            }

            if (string.Equals(ent.Layer, VolumeDisagreementLayer, StringComparison.OrdinalIgnoreCase))
            {
                doomed.Add(id);
            }
        }

        foreach (var id in doomed)
        {
            var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            ent.Erase();
        }
    }

    private static string? SaveVolumeReport(TerrainVolumeResult r, string? drawingPath)
    {
        ProjectFolderPreferences.Load();
        var folder = ProjectFolderPreferences.EnsureFolder();
        var stamp = r.ComputedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var baseFile = $"TCM_ZAPREMINA_{SanitizeFile(r.BaseName)}_vs_{SanitizeFile(r.ComparisonName)}_{stamp}";
        var csvPath = Path.Combine(folder, baseFile + ".csv");
        var htmlPath = Path.Combine(folder, baseFile + ".html");

        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder();
        csv.AppendLine("Metoda;Iskop_m3;Nasip_m3;Neto_m3;PovIskop_m2;PovNasip_m2");
        void Line(TerrainVolumeMethodResult m) =>
            csv.AppendLine(string.Join(";",
                m.MethodName,
                m.CutVolume.ToString("0.000", inv),
                m.FillVolume.ToString("0.000", inv),
                m.NetVolume.ToString("0.000", inv),
                m.CutArea.ToString("0.00", inv),
                m.FillArea.ToString("0.00", inv)));
        Line(r.Tin);
        Line(r.Grid);
        Line(r.Sections);
        csv.AppendLine(string.Join(";",
            "TIN+faktori",
            r.AdjustedCut.ToString("0.000", inv),
            r.AdjustedFill.ToString("0.000", inv),
            r.AdjustedNet.ToString("0.000", inv), "", ""));
        csv.AppendLine();
        csv.AppendLine($"Baza;{r.BaseName}");
        csv.AppendLine($"Poredjenje;{r.ComparisonName}");
        csv.AppendLine($"Poverenje;{r.ConfidenceLevel}");
        csv.AppendLine($"MeanRel%;{r.MeanRelativeErrorPercent.ToString("0.00", inv)}");
        csv.AppendLine($"MaxRel%;{r.MaxRelativeErrorPercent.ToString("0.00", inv)}");
        csv.AppendLine($"Grid_m;{r.GridStep.ToString("0.###", inv)}");
        csv.AppendLine($"Sekcije;{r.SectionCount}");
        csv.AppendLine($"Swell;{r.SwellFactor.ToString("0.###", inv)}");
        csv.AppendLine($"Shrink;{r.ShrinkFactor.ToString("0.###", inv)}");
        csv.AppendLine($"Crtez;{drawingPath ?? ""}");
        csv.AppendLine($"Datum;{r.ComputedAt:yyyy-MM-dd HH:mm:ss}");
        File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        html.AppendLine("<title>TCM-ROADS Zapremina — panel poverenja</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}");
        html.AppendLine("table{border-collapse:collapse;margin:12px 0}th,td{border:1px solid #ccc;padding:6px 10px;text-align:right}");
        html.AppendLine("th{background:#f0f4f8;text-align:left}.ok{background:#e8f5e9}.mid{background:#fff8e1}.bad{background:#ffebee}");
        html.AppendLine("h1{font-size:1.4rem}</style></head><body>");
        html.AppendLine("<h1>TCM-ROADS — Izvestaj zapremine (panel poverenja)</h1>");
        html.AppendLine($"<p><b>Baza (postojece):</b> {Esc(r.BaseName)} &nbsp;→&nbsp; " +
                        $"<b>Poredjenje (projekat):</b> {Esc(r.ComparisonName)}</p>");
        html.AppendLine($"<p><b>Datum:</b> {r.ComputedAt:yyyy-MM-dd HH:mm} &nbsp; " +
                        $"<b>Crtez:</b> {Esc(drawingPath ?? "(nesacuvan)")}</p>");
        var cls = r.ConfidenceLevel.StartsWith("Vis", StringComparison.OrdinalIgnoreCase) ? "ok"
            : r.ConfidenceLevel.StartsWith("Sre", StringComparison.OrdinalIgnoreCase) ? "mid" : "bad";
        html.AppendLine($"<p class=\"{cls}\"><b>Poverenje: {Esc(r.ConfidenceLevel)}</b> — " +
                        $"mean {r.MeanRelativeErrorPercent:0.00}% · max {r.MaxRelativeErrorPercent:0.00}%<br/>" +
                        $"{Esc(r.ConfidenceNote)}</p>");
        if (!string.IsNullOrWhiteSpace(r.Warning))
        {
            html.AppendLine($"<p class=\"mid\"><b>Upozorenje:</b> {Esc(r.Warning)}</p>");
        }

        html.AppendLine("<table><tr><th>Metoda</th><th>Iskop (m³)</th><th>Nasip (m³)</th>" +
                        "<th>Neto (m³)</th><th>Pov. iskop (m²)</th><th>Pov. nasip (m²)</th></tr>");
        void HtmlRow(string name, double cut, double fill, double net, string ca, string fa) =>
            html.AppendLine($"<tr><td style=\"text-align:left\">{Esc(name)}</td>" +
                            $"<td>{cut:0.000}</td><td>{fill:0.000}</td><td>{net:0.000}</td>" +
                            $"<td>{ca}</td><td>{fa}</td></tr>");
        HtmlRow(r.Tin.MethodName, r.Tin.CutVolume, r.Tin.FillVolume, r.Tin.NetVolume,
            r.Tin.CutArea.ToString("0.00", inv), r.Tin.FillArea.ToString("0.00", inv));
        HtmlRow(r.Grid.MethodName, r.Grid.CutVolume, r.Grid.FillVolume, r.Grid.NetVolume,
            r.Grid.CutArea.ToString("0.00", inv), r.Grid.FillArea.ToString("0.00", inv));
        HtmlRow(r.Sections.MethodName, r.Sections.CutVolume, r.Sections.FillVolume, r.Sections.NetVolume,
            r.Sections.CutArea.ToString("0.00", inv), r.Sections.FillArea.ToString("0.00", inv));
        HtmlRow("TIN + faktori", r.AdjustedCut, r.AdjustedFill, r.AdjustedNet, "—", "—");
        html.AppendLine("</table>");
        html.AppendLine("<p><b>Parametri:</b> " +
                        $"grid={r.GridStep:0.###} m, sekcija={r.SectionCount}, " +
                        $"nabujavanje (swell)×{r.SwellFactor:0.###}, " +
                        $"sleganje (shrink)×{r.ShrinkFactor:0.###}<br/>" +
                        $"AOI X {r.MinX:0.##}…{r.MaxX:0.##}, Y {r.MinY:0.##}…{r.MaxY:0.##}</p>");
        html.AppendLine("<p style=\"color:#666;font-size:0.9rem\">Glavni rezultat = TIN–TIN. " +
                        "Grid i sekcije su kontrola. Mapa neslaganja: lejer TCM_ZAP_NESLAGANJE.</p>");
        html.AppendLine("</body></html>");
        File.WriteAllText(htmlPath, html.ToString(), Encoding.UTF8);

        return htmlPath;
    }

    private static string SanitizeFile(string name)
    {
        var chars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            sb.Append(chars.Contains(c) ? '_' : c);
        }

        var s = sb.ToString();
        return string.IsNullOrWhiteSpace(s) ? "Teren" : s;
    }

    private static string Esc(string? s) =>
        (s ?? string.Empty)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
