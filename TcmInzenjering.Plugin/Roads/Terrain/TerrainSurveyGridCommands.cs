using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    private const string SurveyCrossLayer = "TCM_GEO_KRSTOVI";
    private const string SurveyGridPointLayer = "TCM_GEO_RASTER_TACKE";

    /// <summary>
    /// Crta i kotira geodetske krstove u WCS koordinatnom rasteru.
    /// </summary>
    [CommandMethod("TCMGEOKRST", CommandFlags.Modal)]
    public void DrawSurveyCrosses()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        try
        {
            var dialog = new SurveyCrossSettingsDialog();
            if (AcApp.ShowModalWindow(dialog) != true || dialog.Settings is null)
            {
                return;
            }

            var settings = dialog.Settings;
            if (!TryGetGridArea(ed, "geodetskih krstova", out var min, out var max))
            {
                return;
            }

            var grid = BuildGrid(min, max, settings.SpacingX, settings.SpacingY);
            if (!ConfirmLargeGrid(ed, grid.Count, "krstova"))
            {
                return;
            }

            using var tr = doc.Database.TransactionManager.StartTransaction();
            EnsureSurveyLayer(tr, doc.Database, settings.LayerName, settings.LayerColorAci, updateColor: true);
            var textStyleId = EnsureSurveyTextStyle(tr, doc.Database, settings.FontFileName);
            var ms = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),
                OpenMode.ForWrite);

            var half = settings.CrossLength * 0.5;
            var numberFormat = "F" + settings.Decimals.ToString();
            var textColor = Color.FromColorIndex(ColorMethod.ByAci, settings.TextColorAci);
            var gap = Math.Max(settings.TextHeight * 0.35, half * 0.15);
            foreach (var p in grid)
            {
                AppendLine(tr, ms,
                    new Point3d(p.X - half, p.Y, 0),
                    new Point3d(p.X + half, p.Y, 0),
                    settings.LayerName);
                AppendLine(tr, ms,
                    new Point3d(p.X, p.Y - half, 0),
                    new Point3d(p.X, p.Y + half, 0),
                    settings.LayerName);

                if (settings.LabelEast)
                {
                    if (settings.GroupedLabels)
                    {
                        // E desno od krsta, horizontalno, cifre u grupama od 3
                        AppendSurveyCoordinateLabel(
                            tr, ms,
                            FormatGroupedCoordinate("E", p.X, settings.Decimals),
                            new Point3d(p.X + half + gap, p.Y - gap * 0.2, 0),
                            settings.TextHeight,
                            rotation: 0,
                            AttachmentPoint.TopLeft,
                            settings.LayerName,
                            textStyleId,
                            textColor);
                    }
                    else
                    {
                        AppendGridText(
                            tr, ms,
                            $"E={p.X.ToString(numberFormat)}",
                            new Point3d(p.X + half + gap, p.Y - settings.TextHeight * 0.55, 0),
                            settings.TextHeight,
                            0,
                            settings.LayerName,
                            textStyleId,
                            textColor);
                    }
                }

                if (settings.LabelNorth)
                {
                    if (settings.GroupedLabels)
                    {
                        // N iznad krsta, rotirano 90°, cifre u grupama od 3
                        AppendSurveyCoordinateLabel(
                            tr, ms,
                            FormatGroupedCoordinate("N", p.Y, settings.Decimals),
                            new Point3d(p.X - gap * 0.2, p.Y + half + gap, 0),
                            settings.TextHeight,
                            rotation: Math.PI / 2,
                            AttachmentPoint.TopLeft,
                            settings.LayerName,
                            textStyleId,
                            textColor);
                    }
                    else
                    {
                        AppendGridText(
                            tr, ms,
                            $"N={p.Y.ToString(numberFormat)}",
                            new Point3d(p.X - settings.TextHeight * 0.55, p.Y + half + gap, 0),
                            settings.TextHeight,
                            Math.PI / 2,
                            settings.LayerName,
                            textStyleId,
                            textColor);
                    }
                }
            }

            tr.Commit();
            ed.WriteMessage(
                $"\nTCM-ROADS: Nacrtano i kotirano {grid.Count} geodetskih krstova " +
                $"na lejeru {settings.LayerName}.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>
    /// Crta AutoCAD DBPoint tačke u pravilnom WCS koordinatnom rasteru.
    /// </summary>
    [CommandMethod("TCMKOORASTER", CommandFlags.Modal)]
    public void DrawCoordinateGridPoints()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        try
        {
            if (!TryGetGridParameters(
                    ed,
                    "tacaka koordinatnog rastera",
                    out var min,
                    out var max,
                    out var spacingX,
                    out var spacingY))
            {
                return;
            }

            var zResult = ed.GetDouble(new PromptDoubleOptions(
                "\nKota Z raster tacaka <0.00>: ")
            {
                AllowNone = true,
                AllowNegative = true,
                AllowZero = true,
                DefaultValue = 0
            });
            if (zResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            var z = zResult.Status == PromptStatus.OK ? zResult.Value : 0;
            var grid = BuildGrid(min, max, spacingX, spacingY);
            if (!ConfirmLargeGrid(ed, grid.Count, "tacaka"))
            {
                return;
            }

            using var tr = doc.Database.TransactionManager.StartTransaction();
            EnsureSurveyLayer(tr, doc.Database, SurveyGridPointLayer, 1);
            var ms = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),
                OpenMode.ForWrite);

            foreach (var p in grid)
            {
                var point = new DBPoint(new Point3d(p.X, p.Y, z))
                {
                    Layer = SurveyGridPointLayer
                };
                ms.AppendEntity(point);
                tr.AddNewlyCreatedDBObject(point, true);
            }

            tr.Commit();

            // Uočljiv simbol DBPoint, bez pravljenja dodatne geometrije.
            AcApp.SetSystemVariable("PDMODE", 35);
            AcApp.SetSystemVariable("PDSIZE", Math.Max(Math.Min(spacingX, spacingY) * 0.08, 0.2));
            ed.Regen();
            ed.WriteMessage(
                $"\nTCM-ROADS: Nacrtano {grid.Count} DBPoint tacaka koordinatnog rastera " +
                $"na lejeru {SurveyGridPointLayer}.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static bool TryGetGridParameters(
        Editor ed,
        string purpose,
        out Point2d min,
        out Point2d max,
        out double spacingX,
        out double spacingY)
    {
        min = default;
        max = default;
        spacingX = 100;
        spacingY = 100;

        if (!TryGetGridArea(ed, purpose, out min, out max))
        {
            return false;
        }

        var spacingXResult = ed.GetDouble(new PromptDoubleOptions(
            "\nRazmak rastera po X <100.00>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = 100
        });
        if (spacingXResult.Status == PromptStatus.Cancel)
        {
            return false;
        }

        var spacingYResult = ed.GetDouble(new PromptDoubleOptions(
            "\nRazmak rastera po Y <isti kao X>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = spacingXResult.Status == PromptStatus.OK
                ? spacingXResult.Value
                : 100
        });
        if (spacingYResult.Status == PromptStatus.Cancel)
        {
            return false;
        }

        spacingX = spacingXResult.Status == PromptStatus.OK ? spacingXResult.Value : 100;
        spacingY = spacingYResult.Status == PromptStatus.OK ? spacingYResult.Value : spacingX;
        return true;
    }

    private static bool TryGetGridArea(
        Editor ed,
        string purpose,
        out Point2d min,
        out Point2d max)
    {
        min = default;
        max = default;
        var first = ed.GetPoint($"\nPrvi ugao oblasti {purpose}: ");
        if (first.Status != PromptStatus.OK)
        {
            return false;
        }

        var second = ed.GetCorner(new PromptCornerOptions(
            $"\nSuprotni ugao oblasti {purpose}: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            return false;
        }

        min = new Point2d(
            Math.Min(first.Value.X, second.Value.X),
            Math.Min(first.Value.Y, second.Value.Y));
        max = new Point2d(
            Math.Max(first.Value.X, second.Value.X),
            Math.Max(first.Value.Y, second.Value.Y));
        return true;
    }

    private static List<Point2d> BuildGrid(
        Point2d min,
        Point2d max,
        double spacingX,
        double spacingY)
    {
        // Poravnanje na pune koordinatne vrednosti, kao geodetski raster.
        var startX = Math.Ceiling((min.X - 1e-9) / spacingX) * spacingX;
        var startY = Math.Ceiling((min.Y - 1e-9) / spacingY) * spacingY;
        var result = new List<Point2d>();
        for (var x = startX; x <= max.X + 1e-8; x += spacingX)
        {
            for (var y = startY; y <= max.Y + 1e-8; y += spacingY)
            {
                result.Add(new Point2d(x, y));
                if (result.Count > 100_000)
                {
                    return result;
                }
            }
        }

        return result;
    }

    private static bool ConfirmLargeGrid(Editor ed, int count, string noun)
    {
        if (count == 0)
        {
            ed.WriteMessage("\nTCM-ROADS: U izabranoj oblasti nema raster preseka.");
            return false;
        }

        if (count > 100_000)
        {
            ed.WriteMessage(
                "\nTCM-ROADS: Raster je veci od 100.000 elemenata. " +
                "Povecajte razmak ili smanjite oblast.");
            return false;
        }

        if (count <= 10_000)
        {
            return true;
        }

        var options = new PromptKeywordOptions(
            $"\nBice nacrtano {count:N0} {noun}. Nastaviti [Da/Ne] <Ne>: ")
        {
            AllowNone = true
        };
        options.Keywords.Add("Da");
        options.Keywords.Add("Ne");
        options.Keywords.Default = "Ne";
        var result = ed.GetKeywords(options);
        return result.Status == PromptStatus.OK &&
               string.Equals(result.StringResult, "Da", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSurveyLayer(
        Transaction tr,
        Database db,
        string name,
        short aci,
        bool updateColor = false)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(name))
        {
            if (updateColor)
            {
                var existing = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);
                existing.Color = Color.FromColorIndex(ColorMethod.ByAci, aci);
            }

            return;
        }

        table.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = Color.FromColorIndex(ColorMethod.ByAci, aci)
        };
        table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static void AppendLine(
        Transaction tr,
        BlockTableRecord ms,
        Point3d start,
        Point3d end,
        string layer)
    {
        var line = new Line(start, end) { Layer = layer };
        ms.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
    }

    private static ObjectId EnsureSurveyTextStyle(
        Transaction tr,
        Database db,
        string fontFileName)
    {
        const string styleName = "TCM_GEO_KRST_LABEL";
        var resolved = StationFontCatalog.ResolveFileName(fontFileName);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = "arial.ttf";
        }

        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (table.Has(styleName))
        {
            var existing = (TextStyleTableRecord)tr.GetObject(table[styleName], OpenMode.ForWrite);
            if (!string.Equals(existing.FileName, resolved, StringComparison.OrdinalIgnoreCase))
            {
                existing.FileName = resolved;
            }

            return existing.ObjectId;
        }

        table.UpgradeOpen();
        var style = new TextStyleTableRecord
        {
            Name = styleName,
            FileName = resolved
        };
        table.Add(style);
        tr.AddNewlyCreatedDBObject(style, true);
        return style.ObjectId;
    }

    private static void AppendGridText(
        Transaction tr,
        BlockTableRecord ms,
        string value,
        Point3d position,
        double height,
        double rotation,
        string layer,
        ObjectId textStyleId,
        Color color)
    {
        var text = new DBText
        {
            TextString = value,
            Position = position,
            Height = height,
            Rotation = rotation,
            Layer = layer,
            TextStyleId = textStyleId,
            Color = color
        };
        ms.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }

    /// <summary>
    /// Višelinijski N/E natpis (MText) — grupisane cifre kao na Plateia krstovima.
    /// </summary>
    private static void AppendSurveyCoordinateLabel(
        Transaction tr,
        BlockTableRecord ms,
        string mtextContents,
        Point3d position,
        double height,
        double rotation,
        AttachmentPoint attachment,
        string layer,
        ObjectId textStyleId,
        Color color)
    {
        var text = new MText
        {
            Contents = mtextContents,
            Location = position,
            TextHeight = height,
            Rotation = rotation,
            Attachment = attachment,
            Layer = layer,
            TextStyleId = textStyleId,
            Color = color,
            BackgroundFill = false
        };
        // Širina dovoljna za jednu grupu cifara (+ prefix); lom ide samo na \P.
        text.Width = height * 6.5;
        ms.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }

    /// <summary>
    /// Format: "N 4\\P975\\P000" / "E 6\\P604\\P600" — grupe od 3 cifre, prefix N ili E.
    /// </summary>
    private static string FormatGroupedCoordinate(string prefix, double value, int decimals)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rounded = Math.Round(value, Math.Max(0, decimals), MidpointRounding.AwayFromZero);
        var abs = Math.Abs(rounded);
        var intPart = (long)Math.Floor(abs);
        var intDigits = intPart.ToString(inv);

        var groups = new List<string>();
        while (intDigits.Length > 3)
        {
            groups.Insert(0, intDigits[^3..]);
            intDigits = intDigits[..^3];
        }

        if (intDigits.Length > 0)
        {
            groups.Insert(0, intDigits);
        }
        else if (groups.Count == 0)
        {
            groups.Add("0");
        }

        if (decimals > 0)
        {
            var frac = abs - Math.Floor(abs);
            var fracText = frac.ToString("F" + decimals.ToString(inv), inv);
            if (fracText.StartsWith("0.", StringComparison.Ordinal))
            {
                fracText = fracText[1..]; // ".xx"
            }

            groups[^1] += fracText;
        }

        if (rounded < 0)
        {
            groups[0] = "-" + groups[0];
        }

        groups[0] = prefix + " " + groups[0];
        return string.Join("\\P", groups);
    }
}
