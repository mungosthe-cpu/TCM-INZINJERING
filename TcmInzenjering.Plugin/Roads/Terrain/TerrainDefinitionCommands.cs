using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    /// <summary>Dodaje breakline polilinije u definiciju terena (obavezne TIN ivice).</summary>
    [CommandMethod("TCMTERBREAK", CommandFlags.Modal)]
    public void AddTerrainBreaklines()
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
            var opts = new PromptSelectionOptions
            {
                MessageForAdding = "\nIzaberite breakline polilinije (LWPOLY/3DPOLY/LINE): "
            };
            var filter = new SelectionFilter(
            [
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE,LINE")
            ]);

            var sel = ed.GetSelection(opts, filter);
            if (sel.Status != PromptStatus.OK || sel.Value is null || sel.Value.Count == 0)
            {
                ed.WriteMessage("\nTCM-ROADS: Nista nije izabrano.");
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var handles = TerrainDefinitionStore.LoadBreaklineHandles(tr, db).ToList();
            var added = 0;
            foreach (SelectedObject so in sel.Value)
            {
                if (so?.ObjectId.IsNull != false)
                {
                    continue;
                }

                if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is not Curve)
                {
                    continue;
                }

                var h = so.ObjectId.Handle.Value;
                if (!handles.Contains(h))
                {
                    handles.Add(h);
                    added++;
                }
            }

            TerrainDefinitionStore.SaveBreaklineHandles(tr, db, handles);
            tr.Commit();

            ed.WriteMessage(
                $"\nTCM-ROADS: Breakline — dodato {added}, ukupno {handles.Count}. " +
                "Pokrenite TCMTERFACE da primenite na TIN.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>Outer ili Hide granica terena (zatvorena polilinija).</summary>
    [CommandMethod("TCMTERBOUND", CommandFlags.Modal)]
    public void AddTerrainBoundary()
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
            var kw = new PromptKeywordOptions("\nTip granice [Outer/Hide] <Outer>: ")
            {
                AllowNone = true
            };
            kw.Keywords.Add("Outer");
            kw.Keywords.Add("Hide");
            kw.Keywords.Default = "Outer";
            var kwResult = ed.GetKeywords(kw);
            if (kwResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            var kind = kwResult.Status == PromptStatus.OK &&
                       string.Equals(kwResult.StringResult, "Hide", StringComparison.OrdinalIgnoreCase)
                ? TerrainBoundaryKind.Hide
                : TerrainBoundaryKind.Outer;

            var peo = new PromptEntityOptions(
                kind == TerrainBoundaryKind.Outer
                    ? "\nIzaberite zatvorenu poliliniju (Outer boundary): "
                    : "\nIzaberite poliliniju (Hide boundary): ")
            {
                AllowNone = false
            };
            peo.SetRejectMessage("\nPotrebna je kriva (polyline).");
            peo.AddAllowedClass(typeof(Curve), exactMatch: false);

            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(per.ObjectId, OpenMode.ForWrite) is not Curve curve || curve.IsErased)
            {
                ed.WriteMessage("\nTCM-ROADS: Nevalidna kriva.");
                tr.Commit();
                return;
            }

            if (curve is Polyline pl && !pl.Closed && kind == TerrainBoundaryKind.Outer)
            {
                ed.WriteMessage(
                    "\nTCM-ROADS: Outer granica treba zatvorenu poliliniju (Closed=Yes).");
                tr.Commit();
                return;
            }

            var surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db) ?? "Teren_1";
            if (NamedTerrainSurfaceStore.IsBoundaryCompanionName(surfaceName))
            {
                surfaceName = surfaceName[..^("_Granica".Length)];
            }

            RoadDrawing.EnsureRegApp(tr, db);
            EnsureUserBoundaryLayer(tr, db);
            curve.Layer = TerrainUserBoundaryXData.LayerName;
            TerrainUserBoundaryXData.Attach(curve, kind, surfaceName);

            var list = TerrainDefinitionStore.LoadBoundaries(tr, db).ToList();
            var handle = per.ObjectId.Handle.Value;
            list.RemoveAll(b => b.Handle == handle);
            list.Add(new TerrainBoundaryRef(handle, kind));
            // Samo jedna Outer — zameni staru.
            if (kind == TerrainBoundaryKind.Outer)
            {
                list = list.Where(b => b.Kind != TerrainBoundaryKind.Outer || b.Handle == handle).ToList();
            }

            TerrainDefinitionStore.SaveBoundaries(tr, db, list);
            tr.Commit();

            // Dodaj granicu u aktivni (globalni) projekat ako postoji.
            var projectId = TcmProjectStore.GetActiveId();
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                var project = TcmProjectStore.Load(projectId!);
                if (project is not null)
                {
                    project.BoundaryKeys ??= [];
                    var key = TcmProjectStore.FormatBoundaryKey(kind, handle);
                    if (!project.BoundaryKeys.Exists(k =>
                            string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                    {
                        project.BoundaryKeys.Add(key);
                        TcmProjectStore.Save(project);
                    }
                }
            }

            ed.WriteMessage(
                $"\nTCM-ROADS: Granica {kind} na lejeru {TerrainUserBoundaryXData.LayerName}. " +
                "Pokrenite TCMTERFACE. Brisanje linije vraca TIN bez granice.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static void EnsureUserBoundaryLayer(Transaction tr, Database db)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(TerrainUserBoundaryXData.LayerName))
        {
            return;
        }

        lt.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = TerrainUserBoundaryXData.LayerName,
            Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 1)
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>
    /// Nova komanda: linije na izabranom lejeru → spoji postojeće tačke terena duž linije
    /// i preuredi 3DFACE (swap) da trouglovi ne seku tu liniju.
    /// </summary>
    [CommandMethod("TCMTERBRKLAY", CommandFlags.Modal)]
    public void ApplyBreaklinesFromLayer()
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
            if (!TrySelectBreaklineLayers(ed, db, out var layerNames) ||
                !TryGetLayerTolerances(ed, layerNames, out var tolerances))
            {
                return;
            }

            var progressWin = new TcmInzenjering.Plugin.Dialogs.CommandProgressWindow(
                layerNames.Count == 1
                    ? "TCM-ROADS — Breakline lejer"
                    : $"TCM-ROADS — Breakline lejeri ({layerNames.Count})");
            progressWin.Show();
            var progress = progressWin.AsProgress();

            var addedPoints = new List<Point3d>();
            var addedHandles = new List<long>();
            var vertexPoints = new List<Point3d>();
            var vertexStyle = "DBPoint";
            string summary;
            string surfaceName;
            List<Point3d> beforeSurfacePoints;
            List<Point3d> beforeWorkingPoints;
            List<TerrainEdgeKey> beforeForced;
            List<(string Layer, double Tolerance, TerrainLayerBreaklineService.Result Result)> runs = [];
            List<TerrainEdgeKey> trackedSegments = [];
            IReadOnlyList<(Point3d A, Point3d B)> failed = [];
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    progress.Report((3, "Ucitavam teren…"));
                    surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db) ?? "Teren_1";
                    if (NamedTerrainSurfaceStore.IsBoundaryCompanionName(surfaceName))
                    {
                        surfaceName = surfaceName[..^("_Granica".Length)];
                    }

                    beforeSurfacePoints = (NamedTerrainSurfaceStore.TryLoadSurface(
                        tr, db, surfaceName) ?? Array.Empty<Point3d>()).ToList();
                    beforeWorkingPoints = TerrainPointStore.Load(tr, db).ToList();
                    beforeForced = TerrainDefinitionStore.LoadForcedEdges(tr, db).ToList();
                    var points = beforeSurfacePoints.Count >= 3
                        ? beforeSurfacePoints
                        : beforeWorkingPoints;
                    if (points.Count < 3)
                    {
                        ed.WriteMessage("\nTCM-ROADS: Nema dovoljno tacaka terena. Prvo TCMTERTACKE / TCMTERFACE.");
                        tr.Commit();
                        return;
                    }

                    for (var layerIndex = 0; layerIndex < layerNames.Count; layerIndex++)
                    {
                        var layer = layerNames[layerIndex];
                        var tol = tolerances[layer];
                        progress.Report((
                            3 + (int)(45.0 * layerIndex / layerNames.Count),
                            $"Lejer {layerIndex + 1}/{layerNames.Count}: „{layer}“ — mapiram tacke…"));
                        var result = TerrainLayerBreaklineService.Apply(
                            tr, db, layer, points, tol, progress);
                        runs.Add((layer, tol, result));
                        foreach (var candidate in result.CandidateEdges)
                        {
                            if (!trackedSegments.Any(e => e.Matches(candidate.A, candidate.B)))
                            {
                                trackedSegments.Add(candidate);
                            }
                        }
                    }

                    // Tacke loma (spoj dve linije bez tacke terena, Z interpolisana):
                    // dodaj ih u teren i nacrtaj kao i postojece tacke, da TIN moze da
                    // prati liniju i kroz lom.
                    vertexPoints = runs
                        .SelectMany(r => r.Result.VertexPointsAdded)
                        .Where(v => !points.Any(p =>
                            Math.Abs(p.X - v.X) <= 1e-6 && Math.Abs(p.Y - v.Y) <= 1e-6))
                        .ToList();
                    if (vertexPoints.Count > 0)
                    {
                        EnsureTerrainLayer(tr, db);
                        var msRec = (BlockTableRecord)tr.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(db),
                            OpenMode.ForWrite);
                        var updatedPoints = points.ToList();
                        foreach (var v in vertexPoints)
                        {
                            updatedPoints.Add(v);
                            addedPoints.Add(v);
                            var (_, styleLabel, handle) =
                                TerrainBoundaryPointDrawer.InsertTerrainPoint(
                                    tr, db, msRec, v, TerrainLayerName);
                            vertexStyle = styleLabel;
                            if (handle != 0)
                            {
                                addedHandles.Add(handle);
                            }
                        }

                        NamedTerrainSurfaceStore.SaveSurface(tr, db, surfaceName, updatedPoints);
                        TerrainPointStore.Save(tr, db, updatedPoints);
                    }

                    tr.Commit();
                }

                if (runs.All(r => r.Result.Curves == 0))
                {
                    ed.WriteMessage("\nTCM-ROADS: Nema LINE/POLYLINE na izabranim lejerima.");
                    return;
                }

                if (trackedSegments.Count == 0)
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Nema parova tacaka za breakline ivice. " +
                        "Povecajte toleranciju ili proverite geometriju linija.");
                    return;
                }

                var totalMatched = runs.Sum(r => r.Result.PointsMatched);
                var totalEdgesAdded = runs.Sum(r => r.Result.EdgesAdded);
                ed.WriteMessage(
                    $"\nTCM-ROADS: {layerNames.Count} lejer(a) — {totalMatched} uparenih tacaka, " +
                    $"{totalEdgesAdded} novih fors. ivica. " +
                    "Preuređujem 3DFACE…");
                var allFailed = RebuildTerrainFacesPublic(ed, db, announce: true, surfaceName, progress);
                failed = FilterTrackedFailures(allFailed, trackedSegments);
                var initialFailed = failed.Count;

                // Segmenti koje swap ne može da ugradi: ubaci interpolisanu tačku
                // na sredini segmenta duž linije lejera, pa pokušaj ponovo.
                var pointStyle = vertexStyle;
                const int maxRetries = 3;
                for (var attempt = 0; attempt < maxRetries && failed.Count > 0; attempt++)
                {
                    progress.Report((95, $"Dodajem tacke na neuspele segmente ({failed.Count})…"));
                    var inserted = InsertBreaklineMidpoints(
                        db, tolerances, failed, surfaceName, addedPoints, addedHandles,
                        trackedSegments, out var styleLabel);
                    if (inserted == 0)
                    {
                        break;
                    }

                    pointStyle = styleLabel;
                    ed.WriteMessage(
                        $"\nTCM-ROADS: Dodato {inserted} interpolisanih tacaka — novi pokusaj…");
                    allFailed = RebuildTerrainFacesPublic(ed, db, announce: false, surfaceName, progress);
                    failed = FilterTrackedFailures(allFailed, trackedSegments);
                }

                progress.Report((100, "Gotovo."));

                var layerSummary = string.Join("\n", runs.Select(r =>
                    $"• „{r.Layer}“ (tol. {r.Tolerance:0.###} m): " +
                    $"{r.Result.Curves} linija, {r.Result.PointsMatched} tacaka, " +
                    $"{r.Result.CandidateEdges.Count} ivica ({r.Result.EdgesAdded} novih)"));
                var midpointCount = addedPoints.Count - vertexPoints.Count;
                summary =
                    $"Obradjeno lejera: {runs.Count}\n\n{layerSummary}\n\n" +
                    (vertexPoints.Count > 0
                        ? $"• Dodato tacaka loma: {vertexPoints.Count}\n" +
                          "  — Na spoju dve linije (kraj jedne = pocetak druge) nije\n" +
                          "    postojala tacka terena, pa je dodata nova; visina je\n" +
                          "    interpolisana izmedju najblizih tacaka na obe linije.\n" +
                          "  — Koordinate su u tabeli ispod.\n"
                        : string.Empty) +
                    (initialFailed > 0
                        ? $"• Segmenata koji nisu mogli odmah: {initialFailed}\n"
                        : "• Sve ivice su ugradjene u TIN iz prvog pokusaja.\n") +
                    (midpointCount > 0
                        ? $"• Dodato novih tacaka (interpolacija po liniji): {midpointCount}\n"
                        : string.Empty) +
                    (addedPoints.Count > 0
                        ? $"  — {pointStyle} na lejeru {TerrainLayerName}, kao postojece tacke terena.\n" +
                          (pointStyle == "DBPoint"
                              ? "  — Napomena: blok sa visinom nije definisan; pokrenite TCMTERBLOK\n" +
                                "    da nove tacke dobiju blok sa ispisanom kotom kao ostale.\n"
                              : string.Empty)
                        : string.Empty) +
                    (failed.Count > 0
                        ? $"\nUPOZORENJE: {failed.Count} segmenata i dalje nije ugradjeno. " +
                          "Povecajte toleranciju ili proverite geometriju linije."
                        : "\nTIN prati liniju lejera celom duzinom.");
            }
            finally
            {
                try
                {
                    progressWin.Close();
                }
                catch
                {
                    // ignore
                }
            }

            var summaryDlg = new TcmInzenjering.Plugin.Dialogs.BreaklineLayerSummaryDialog(
                summary,
                addedPoints,
                failed,
                addedPoints.Count == 0
                    ? null
                    : () => UndoBreaklineLayerRun(
                        ed, db, surfaceName,
                        beforeSurfacePoints, beforeWorkingPoints, beforeForced, addedHandles),
                saveToProject: () =>
                {
                    if (string.IsNullOrWhiteSpace(surfaceName))
                    {
                        return "Nema aktivnog imenovanog terena za snimanje.";
                    }

                    return TcmProjectStore.AddPointSetToActiveProject(surfaceName)
                           ?? $"Skup „{surfaceName}“ snimljen u aktivni TCM projekat.";
                });
            AcApp.ShowModalWindow(summaryDlg);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static bool TrySelectBreaklineLayers(
        Editor editor,
        Database db,
        out List<string> layerNames)
    {
        layerNames = [];
        var options = new PromptEntityOptions(
            "\nIzaberite liniju breakline lejera ili [Ime]: ")
        {
            AllowNone = false
        };
        options.Keywords.Add("Ime");
        options.SetRejectMessage("\nPotrebna je kriva (LINE/POLYLINE).");
        options.AddAllowedClass(typeof(Curve), exactMatch: false);
        var result = editor.GetEntity(options);

        if (result.Status == PromptStatus.Keyword &&
            string.Equals(result.StringResult, "Ime", StringComparison.OrdinalIgnoreCase))
        {
            var stringResult = editor.GetString(new PromptStringOptions(
                "\nIme breakline lejera: ")
            {
                AllowSpaces = true
            });
            if (stringResult.Status != PromptStatus.OK ||
                string.IsNullOrWhiteSpace(stringResult.StringResult))
            {
                return false;
            }

            layerNames.Add(stringResult.StringResult.Trim());
            return true;
        }

        if (result.Status != PromptStatus.OK)
        {
            return false;
        }

        using var tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(result.ObjectId, OpenMode.ForRead) is not Entity entity)
        {
            return false;
        }

        layerNames.Add(entity.Layer);
        tr.Commit();
        return true;
    }

    private static bool TryGetLayerTolerances(
        Editor editor,
        IReadOnlyList<string> layers,
        out Dictionary<string, double> tolerances)
    {
        tolerances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            var defaultValue = BreaklineLayerPreferences.Get(layer);
            var result = editor.GetDouble(new PromptDoubleOptions(
                $"\nTolerancija XY za lejer „{layer}“ (m) <{defaultValue:0.###}>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                AllowNone = true,
                DefaultValue = defaultValue
            });
            if (result.Status == PromptStatus.Cancel)
            {
                return false;
            }

            var value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            tolerances[layer] = value;
            BreaklineLayerPreferences.Set(layer, value);
        }

        return true;
    }

    private static IReadOnlyList<(Point3d A, Point3d B)> FilterTrackedFailures(
        IReadOnlyList<(Point3d A, Point3d B)> failures,
        IReadOnlyList<TerrainEdgeKey> tracked) =>
        failures
            .Where(f => tracked.Any(t => t.Matches(f.A, f.B)))
            .ToList();

    private static string? UndoBreaklineLayerRun(
        Editor editor,
        Database db,
        string surfaceName,
        IReadOnlyList<Point3d> beforeSurfacePoints,
        IReadOnlyList<Point3d> beforeWorkingPoints,
        IReadOnlyList<TerrainEdgeKey> beforeForced,
        IReadOnlyList<long> addedHandles)
    {
        try
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                NamedTerrainSurfaceStore.SaveSurface(
                    tr, db, surfaceName, beforeSurfacePoints, setActive: true);
                TerrainPointStore.Save(tr, db, beforeWorkingPoints);
                TerrainDefinitionStore.SaveForcedEdges(tr, db, beforeForced);

                foreach (var handleValue in addedHandles.Distinct())
                {
                    try
                    {
                        if (!db.TryGetObjectId(new Handle(handleValue), out var id) ||
                            id.IsNull || id.IsErased)
                        {
                            continue;
                        }

                        if (tr.GetObject(id, OpenMode.ForWrite, openErased: false) is Entity entity &&
                            !entity.IsErased)
                        {
                            entity.Erase();
                        }
                    }
                    catch
                    {
                        // Jedan već obrisan marker ne sme sprečiti ostatak Undo-a.
                    }
                }

                tr.Commit();
            }

            RebuildTerrainFacesPublic(editor, db, announce: false, surfaceName);
            editor.Regen();
            return null;
        }
        catch (System.Exception ex)
        {
            return $"Ponistavanje nije uspelo: {ex.Message}";
        }
    }

    /// <summary>
    /// Za neuspele forsirane segmente dodaje tačku na sredini segmenta duž linije lejera
    /// (Z linearno interpolisana) i zamenjuje ivicu a–b sa a–m i m–b.
    /// </summary>
    private static int InsertBreaklineMidpoints(
        Database db,
        IReadOnlyDictionary<string, double> layerTolerances,
        IReadOnlyList<(Point3d A, Point3d B)> failedEdges,
        string surfaceName,
        List<Point3d> addedPoints,
        List<long> addedHandles,
        List<TerrainEdgeKey> trackedSegments,
        out string pointStyleLabel)
    {
        pointStyleLabel = "DBPoint";
        var inserted = 0;
        using var tr = db.TransactionManager.StartTransaction();
        var layerCurves = layerTolerances.ToDictionary(
            pair => pair.Key,
            pair => TerrainLayerBreaklineService.CollectCurvesOnLayer(tr, db, pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var points = (NamedTerrainSurfaceStore.TryLoadSurface(tr, db, surfaceName)
                      ?? TerrainPointStore.Load(tr, db)).ToList();
        var forced = TerrainDefinitionStore.LoadForcedEdges(tr, db).ToList();

        EnsureTerrainLayer(tr, db);
        var ms = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        foreach (var (a, b) in failedEdges)
        {
            Point3d? mid = null;
            foreach (var pair in layerTolerances)
            {
                mid = ComputeMidpointOnLayer(layerCurves[pair.Key], a, b, pair.Value);
                if (mid is not null)
                {
                    break;
                }
            }

            if (mid is null)
            {
                continue;
            }

            var m = mid.Value;
            if (points.Any(p =>
                    Math.Abs(p.X - m.X) <= 1e-6 && Math.Abs(p.Y - m.Y) <= 1e-6))
            {
                continue;
            }

            points.Add(m);
            addedPoints.Add(m);
            inserted++;

            // Isti format kao postojece tacke na crtezu: TCMTERBLOK blok sa
            // ispisanom visinom ako je definisan, inace DBPoint na TCM_TEREN.
            var (_, styleLabel, handle) = TerrainBoundaryPointDrawer.InsertTerrainPoint(
                tr, db, ms, m, TerrainLayerName);
            pointStyleLabel = styleLabel;
            if (handle != 0)
            {
                addedHandles.Add(handle);
            }

            forced.RemoveAll(e => e.Matches(a, b));
            trackedSegments.RemoveAll(e => e.Matches(a, b));
            var half1 = TerrainEdgeKey.Create(a, m);
            var half2 = TerrainEdgeKey.Create(m, b);
            if (!forced.Any(e => e.Matches(half1.A, half1.B)))
            {
                forced.Add(half1);
            }

            if (!forced.Any(e => e.Matches(half2.A, half2.B)))
            {
                forced.Add(half2);
            }

            trackedSegments.Add(half1);
            trackedSegments.Add(half2);
        }

        if (inserted > 0)
        {
            NamedTerrainSurfaceStore.SaveSurface(tr, db, surfaceName, points);
            TerrainPointStore.Save(tr, db, points);
            TerrainDefinitionStore.SaveForcedEdges(tr, db, forced);
        }

        tr.Commit();
        return inserted;
    }

    /// <summary>Sredina segmenta a–b duž krive lejera; Z = srednja vrednost (linearna interpolacija).</summary>
    private static Point3d? ComputeMidpointOnLayer(
        IReadOnlyList<Curve> curves,
        Point3d a,
        Point3d b,
        double tol)
    {
        foreach (var curve in curves)
        {
            if (!TryGetDistOnCurve(curve, a, tol, out var da) ||
                !TryGetDistOnCurve(curve, b, tol, out var dbDist))
            {
                continue;
            }

            if (Math.Abs(dbDist - da) <= 1e-9)
            {
                continue;
            }

            try
            {
                var pm = curve.GetPointAtDist((da + dbDist) * 0.5);
                return new Point3d(pm.X, pm.Y, (a.Z + b.Z) * 0.5);
            }
            catch
            {
                // probaj sledecu krivu
            }
        }

        // Ne dodaj tačku ako segment zaista ne pripada ovom lejeru.
        return null;
    }

    private static bool TryGetDistOnCurve(Curve curve, Point3d p, double tol, out double dist)
    {
        dist = 0;
        try
        {
            var closest = curve.GetClosestPointTo(p, false);
            var dx = closest.X - p.X;
            var dy = closest.Y - p.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > tol)
            {
                return false;
            }

            dist = curve.GetDistAtPoint(closest);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Brise sacuvane TIN edit operacije (swap/delete), ne dira breakline/granice.</summary>
    [CommandMethod("TCMTEREDCLEAR", CommandFlags.Modal)]
    public void ClearTerrainTinEdits()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            TerrainDefinitionStore.ClearEditOps(tr, doc.Database);
            tr.Commit();
            doc.Editor.WriteMessage(
                "\nTCM-ROADS: TIN edit ops obrisani. TCMTERFACE koristi samo tacke + breakline/granicu.");
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }
}
