#if !BRICSCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
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

        var axisName = PromptAxisName(ed, db);
        if (axisName is null)
        {
            return;
        }

        var defaults = BuildDefaults(null);

        while (true)
        {
            RoadAxis? axis;
            RoadAxisMetadata? metadata;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                metadata = RoadAxisStore.Load(tr, db, axisName);
                if (metadata is null)
                {
                    ed.WriteMessage($"\nTCM-INZINJERING: osovina '{axisName}' nije pronadjena.");
                    tr.Commit();
                    return;
                }

                axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
                if (axis is null || axis.Elements.Count == 0)
                {
                    ed.WriteMessage($"\nTCM-INZINJERING: nema nacrtane osovine '{axisName}'.");
                    tr.Commit();
                    return;
                }

                if (defaults.Station <= axis.StartStation)
                {
                    defaults.Station = (axis.StartStation + axis.Elements[^1].EndStation) / 2.0;
                }

                defaults.Prefix = metadata.Prefix;
                defaults.CounterStart = metadata.AxisCounterStart;
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
                        "Izaberite tačku na tangenti osovine:",
                        out var pickedStation))
                {
                    defaults.Station = pickedStation;
                    ed.WriteMessage(
                        $"\nTCM-INZINJERING: Izabrana stacionaža {pickedStation:0.###}. Potvrdite sa OK ili nastavite sa „Crtaj više…“.");
                }

                continue;
            }

            if (dialog.CloseAction == CrossAxisDrawCloseAction.DrawMultipleInDrawing)
            {
                if (!defaults.AutoNaming)
                {
                    System.Windows.MessageBox.Show(
                        "Crtanje više poprečnih osa radi samo sa automatskim imenovanjem.",
                        "TCM-INŽINJERING — Poprečna osa",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    continue;
                }

                axis = ReadAxis(db, axisName, metadata!.StartStation);
                if (axis is null)
                {
                    break;
                }

                var count = AxisStationPicker.TryPickMultipleStations(doc, axis, out var stations);
                string? multiError = null;
                if (count > 0 &&
                    TryDrawAxes(doc, ed, db, axisName, defaults, stations, out var drawn, out multiError))
                {
                    ed.WriteMessage(
                        $"\nTCM-INZINJERING: Dodato {drawn} poprečnih osa.");
                }
                else if (!string.IsNullOrWhiteSpace(multiError))
                {
                    ed.WriteMessage($"\nTCM-INZINJERING: {multiError}");
                    System.Windows.MessageBox.Show(
                        multiError,
                        "TCM-INŽINJERING — Poprečna osa",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }

                continue;
            }

            if (TryDrawAxes(doc, ed, db, axisName, defaults, [defaults.Station], out _, out var singleError))
            {
                ed.WriteMessage(
                    $"\nTCM-INZINJERING: Dodata poprečna osa na stacionaži {defaults.Station:0.###}.");
                break;
            }

            if (!string.IsNullOrWhiteSpace(singleError))
            {
                ed.WriteMessage($"\nTCM-INZINJERING: {singleError}");
                System.Windows.MessageBox.Show(
                    singleError,
                    "TCM-INŽINJERING — Poprečna osa",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
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
                var single = new CrossAxisDrawParameters
                {
                    Station = stations[0],
                    LeftWidth = parameters.LeftWidth,
                    RightWidth = parameters.RightWidth,
                    AutoNaming = parameters.AutoNaming,
                    Prefix = parameters.Prefix,
                    CounterStart = parameters.CounterStart,
                    IncreasingNumbers = parameters.IncreasingNumbers,
                    FixedName = parameters.FixedName
                };

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
        return new CrossAxisDrawParameters
        {
            Station = 0,
            LeftWidth = StationFontPreferences.CrossAxisLeftLength,
            RightWidth = StationFontPreferences.CrossAxisRightLength,
            AutoNaming = true,
            Prefix = metadata?.Prefix ?? "STA ",
            CounterStart = metadata?.AxisCounterStart ?? 1,
            IncreasingNumbers = true
        };
    }

    private static string? PromptAxisName(Editor ed, Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var names = RoadAxisStore.GetAxisNames(tr, db);
        tr.Commit();

        if (names.Count == 0)
        {
            ed.WriteMessage("\nTCM-INZINJERING: Nema definisanih osovine.");
            return null;
        }

        if (names.Count == 1)
        {
            return names[0];
        }

        var options = new PromptStringOptions(
            $"\nIme osovine <{names[0]}>: ")
        {
            AllowSpaces = true
        };
        options.DefaultValue = names[0];
        var result = ed.GetString(options);
        if (result.Status != PromptStatus.OK)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(result.StringResult) ? names[0] : result.StringResult.Trim();
        return names.Contains(name, StringComparer.OrdinalIgnoreCase) ? name : names[0];
    }
}
#endif
