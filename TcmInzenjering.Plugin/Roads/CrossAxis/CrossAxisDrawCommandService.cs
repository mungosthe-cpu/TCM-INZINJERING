#if !BRICSCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.CrossAxis;
using TcmInzenjering.Plugin.Roads.Profile;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

internal static class CrossAxisDrawCommandService
{
    public static void Run(Document? doc)
    {
        doc ??= AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        if (!TryResolveAxisAndDefaults(doc, ed, db, out var axisName, out var defaults))
        {
            return;
        }

        while (true)
        {
            RoadAxis? axis;
            RoadAxisMetadata? metadata;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                metadata = RoadAxisStore.Load(tr, db, axisName);
                if (metadata is null)
                {
                    ed.WriteMessage($"\nTCM-ROADS: osovina '{axisName}' nije pronadjena.");
                    tr.Commit();
                    return;
                }

                axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
                if (axis is null || axis.Elements.Count == 0)
                {
                    ed.WriteMessage($"\nTCM-ROADS: nema nacrtane osovine '{axisName}'.");
                    tr.Commit();
                    return;
                }

                if (defaults.Station <= axis.StartStation)
                {
                    defaults.Station = (axis.StartStation + axis.Elements[^1].EndStation) / 2.0;
                }

                if (string.IsNullOrWhiteSpace(defaults.Prefix))
                {
                    defaults.Prefix = metadata.Prefix;
                }

                if (defaults.CounterStart <= 0)
                {
                    defaults.CounterStart = metadata.AxisCounterStart;
                }

                tr.Commit();
            }

            var axisEnd = axis.Elements[^1].EndStation;
            var dialog = new CrossAxisDrawDialog(axis.StartStation, axisEnd, defaults);
            var dialogResult = dialog.ShowDialog();

            if (dialog.CloseAction == CrossAxisDrawCloseAction.Cancelled ||
                (dialogResult != true && dialog.CloseAction != CrossAxisDrawCloseAction.PickStation &&
                 dialog.CloseAction != CrossAxisDrawCloseAction.DrawMultipleInDrawing))
            {
                break;
            }

            defaults = dialog.Result.Parameters;

            if (dialog.CloseAction == CrossAxisDrawCloseAction.PickStation)
            {
                if (AxisStationPicker.TryPickStation(
                        doc,
                        axis,
                        metadata,
                        "Izaberite tačku na osovini:",
                        out var pickedStation,
                        defaults.LeftWidth,
                        defaults.RightWidth))
                {
                    defaults.Station = pickedStation;
                    ed.WriteMessage(
                        $"\nTCM-ROADS: Izabrana stacionaža {pickedStation:0.###}. Potvrdite sa OK ili nastavite sa „Crtaj više…“.");
                }

                continue;
            }

            if (dialog.CloseAction == CrossAxisDrawCloseAction.DrawMultipleInDrawing)
            {
                if (!defaults.AutoNaming)
                {
                    System.Windows.MessageBox.Show(
                        "Crtanje više poprečnih osa radi samo sa automatskim imenovanjem.",
                        "TCM-ROADS — Poprečna osa",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    continue;
                }

                axis = ReadAxis(db, axisName, metadata!.StartStation);
                if (axis is null)
                {
                    break;
                }

                var count = AxisStationPicker.TryPickMultipleStations(
                    doc,
                    axis,
                    out var stations,
                    metadata,
                    defaults.LeftWidth,
                    defaults.RightWidth);
                string? multiError = null;
                if (count > 0 &&
                    TryDrawAxes(doc, ed, db, axisName, defaults, stations, out var drawn, out multiError))
                {
                    ed.WriteMessage(
                        $"\nTCM-ROADS: Dodato {drawn} poprečnih osa.");
                }
                else if (!string.IsNullOrWhiteSpace(multiError))
                {
                    ed.WriteMessage($"\nTCM-ROADS: {multiError}");
                    System.Windows.MessageBox.Show(
                        multiError,
                        "TCM-ROADS — Poprečna osa",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }

                continue;
            }

            if (TryDrawAxes(doc, ed, db, axisName, defaults, [defaults.Station], out _, out var singleError))
            {
                ed.WriteMessage(
                    $"\nTCM-ROADS: Dodata poprečna osa na stacionaži {defaults.Station:0.###}.");
                break;
            }

            if (!string.IsNullOrWhiteSpace(singleError))
            {
                ed.WriteMessage($"\nTCM-ROADS: {singleError}");
                System.Windows.MessageBox.Show(
                    singleError,
                    "TCM-ROADS — Poprečna osa",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
    }

    private static bool TryResolveAxisAndDefaults(
        Document doc,
        Editor ed,
        Database db,
        out string axisName,
        out CrossAxisDrawParameters defaults)
    {
        axisName = string.Empty;
        defaults = BuildDefaults(null);

        IReadOnlyList<string> names;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            names = RoadAxisStore.GetAxisNames(tr, db);
            tr.Commit();
        }

        if (names.Count == 0)
        {
            ed.WriteMessage("\nTCM-ROADS: Nema definisanih osovina.");
            return false;
        }

        while (true)
        {
            var chooser = new AxisSelectionDialog(names);
            AcApp.ShowModalWindow(chooser);

            if (chooser.CloseAction == AxisSelectionCloseAction.Cancelled)
            {
                return false;
            }

            if (chooser.CloseAction == AxisSelectionCloseAction.PickInDrawing)
            {
                if (!TryPickAxisInDrawing(ed, db, out axisName))
                {
                    continue;
                }
            }
            else if (chooser.CloseAction == AxisSelectionCloseAction.Selected)
            {
                axisName = chooser.SelectedAxisName;
            }
            else
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(axisName))
            {
                continue;
            }

            CrossAxisTemplateStyle style;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var metadata = RoadAxisStore.Load(tr, db, axisName);
                if (metadata is null)
                {
                    tr.Commit();
                    ed.WriteMessage($"\nTCM-ROADS: Osovina '{axisName}' nije pronađena.");
                    continue;
                }

                style = CrossAxisStyleReader.ReadFromRoadAxis(tr, db, axisName, metadata);
                tr.Commit();
            }

            StationFontPreferences.Save(style.FontFileName, style.LeftWidth, style.RightWidth);
            defaults = new CrossAxisDrawParameters
            {
                Station = 0,
                LeftWidth = style.LeftWidth,
                RightWidth = style.RightWidth,
                AutoNaming = true,
                Prefix = style.Prefix,
                CounterStart = 1,
                IncreasingNumbers = true,
                TextHeightOverride = style.TextHeight,
                FontFileNameOverride = style.FontFileName
            };

            ed.WriteMessage(
                $"\nTCM-ROADS: Osovina '{axisName}' — dužine L={style.LeftWidth:0.###} / D={style.RightWidth:0.###}, " +
                $"H={style.TextHeight:0.###}, font={style.FontFileName}.");

            ZoomToRoadAxis(doc, axisName);
            return true;
        }
    }

    private static bool TryPickAxisInDrawing(Editor ed, Database db, out string axisName)
    {
        axisName = string.Empty;
        var options = new PromptEntityOptions(
            "\nIzaberite osovinu u crtežu: ")
        {
            AllowNone = false
        };
        options.SetRejectMessage("\nIzaberite Line, Arc ili LWPOLYLINE osovine.");
        options.AddAllowedClass(typeof(Line), exactMatch: false);
        options.AddAllowedClass(typeof(Arc), exactMatch: false);
        options.AddAllowedClass(typeof(Polyline), exactMatch: false);
        var result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
        {
            return false;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var entity = tr.GetObject(result.ObjectId, OpenMode.ForRead) as Entity;
        if (entity is null)
        {
            tr.Commit();
            return false;
        }

        if (RoadXData.TryReadSourcePolyline(entity, out axisName) ||
            RoadXData.TryReadAxisElement(entity, out axisName, out _) ||
            RoadXData.TryReadProjectedAxis(entity, out axisName))
        {
            tr.Commit();
            return !string.IsNullOrWhiteSpace(axisName);
        }

        if (CrossAxis.CrossAxisXData.TryReadCrossAxis(entity, out _, out var parent) &&
            !string.IsNullOrWhiteSpace(parent))
        {
            axisName = parent.Trim();
            tr.Commit();
            return true;
        }

        tr.Commit();
        ed.WriteMessage("\nTCM-ROADS: Nije TCM osovina.");
        return false;
    }

    private static void ZoomToRoadAxis(Document doc, string axisName)
    {
        var ed = doc.Editor;
        var db = doc.Database;
        var ids = new List<ObjectId>();
        Extents3d? combined = null;

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForRead);
            foreach (ObjectId id in modelSpace)
            {
                if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
                {
                    continue;
                }

                if (!RoadXData.TryReadAxisElement(entity, out var name, out _) ||
                    !string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var extents = entity.GeometricExtents;
                    if (combined is null)
                    {
                        combined = extents;
                    }
                    else
                    {
                        var value = combined.Value;
                        value.AddExtents(extents);
                        combined = value;
                    }

                    ids.Add(id);
                }
                catch
                {
                    // skip entities without extents
                }
            }

            tr.Commit();
        }

        if (combined is null)
        {
            ed.WriteMessage($"\nTCM-ROADS: Geometrija osovine '{axisName}' nije pronađena za zum.");
            return;
        }

        ed.SetImpliedSelection(ids.ToArray());
        ZoomToExtents(ed, combined.Value, 0.08);
        ed.UpdateScreen();
        ed.WriteMessage($"\nTCM-ROADS: Zumirano na osovinu '{axisName}'.");
    }

    private static void ZoomToExtents(Editor editor, Extents3d extents, double marginRatio)
    {
        using var view = editor.GetCurrentView();
        var worldToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
        worldToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * worldToDcs;
        worldToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * worldToDcs;
        extents.TransformBy(worldToDcs.Inverse());

        var width = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, 1.0);
        var height = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, 1.0);
        var margin = Math.Max(0, marginRatio);
        view.Width = width * (1 + 2 * margin);
        view.Height = height * (1 + 2 * margin);
        view.CenterPoint = new Point2d(
            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
        editor.SetCurrentView(view);
    }

    private static bool TryDrawAxes(
        Document doc,
        Editor ed,
        Database db,
        string axisName,
        CrossAxisDrawParameters parameters,
        IReadOnlyList<double> stations,
        out int drawn,
        out string? error)
    {
        drawn = 0;
        error = null;

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var metadata = RoadAxisStore.Load(tr, db, axisName);
            var axis = metadata is null
                ? null
                : AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
            if (axis is null || metadata is null)
            {
                tr.Abort();
                error = "Osovina nije pronadjena.";
                return false;
            }

            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            if (stations.Count == 1)
            {
                var single = CloneParameters(parameters, stations[0]);
                if (!CrossAxisDrawService.TryDrawAtStation(
                        tr,
                        db,
                        modelSpace,
                        axisName,
                        axis,
                        metadata,
                        single,
                        out error))
                {
                    tr.Abort();
                    return false;
                }

                drawn = 1;
            }
            else if (!CrossAxisDrawService.TryDrawMultipleAtStations(
                         tr,
                         db,
                         modelSpace,
                         axisName,
                         axis,
                         metadata,
                         parameters,
                         stations,
                         out drawn,
                         out error))
            {
                if (drawn == 0)
                {
                    tr.Abort();
                    return false;
                }
            }

            tr.Commit();
            if (drawn > 0)
            {
                // Pun redraw profila na Idle — ne blokira OK.
                ProfileViewRefresh.ScheduleIfExists(doc, axisName);
            }

            return drawn > 0;
        }
    }

    private static CrossAxisDrawParameters CloneParameters(
        CrossAxisDrawParameters source,
        double station) =>
        new()
        {
            Station = station,
            LeftWidth = source.LeftWidth,
            RightWidth = source.RightWidth,
            AutoNaming = source.AutoNaming,
            Prefix = source.Prefix,
            CounterStart = source.CounterStart,
            IncreasingNumbers = source.IncreasingNumbers,
            FixedName = source.FixedName,
            TextHeightOverride = source.TextHeightOverride,
            FontFileNameOverride = source.FontFileNameOverride
        };

    private static RoadAxis? ReadAxis(Database db, string axisName, double startStation)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
        tr.Commit();
        return axis;
    }

    private static CrossAxisDrawParameters BuildDefaults(RoadAxisMetadata? metadata)
    {
        StationFontPreferences.Load();
        var half = metadata is { TickLength: > 1e-6 }
            ? Math.Max(0.1, metadata.TickLength * 0.5)
            : StationFontPreferences.CrossAxisLeftLength;
        return new CrossAxisDrawParameters
        {
            Station = 0,
            LeftWidth = half,
            RightWidth = metadata is { TickLength: > 1e-6 }
                ? half
                : StationFontPreferences.CrossAxisRightLength,
            AutoNaming = true,
            Prefix = metadata?.Prefix ?? "STA ",
            CounterStart = metadata?.AxisCounterStart ?? 1,
            IncreasingNumbers = true,
            TextHeightOverride = metadata?.TextHeight ?? 0
        };
    }
}
#endif
