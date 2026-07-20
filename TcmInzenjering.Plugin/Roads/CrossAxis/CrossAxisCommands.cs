using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if !BRICSCAD
using TcmInzenjering.Plugin.Dialogs;
#endif
using TcmInzenjering.Plugin.Roads.CrossAxis;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed class CrossAxisCommandService
{
    public static void Run(Document? doc)
    {
        if (doc is null)
        {
            return;
        }

#if BRICSCAD
        doc.Editor.WriteMessage("\nTCM-ROADS: Podesavanje poprecnih osa nije dostupno u BricsCAD verziji.");
        return;
#else
        RunDialogLoop(doc);
#endif
    }

#if !BRICSCAD
    private static CrossAxisPlacementSettings? _pendingSettings;
    private static IReadOnlyList<long>? _pendingSelection;
    private static bool? _pendingLengthsEnabled;
    private static double? _pendingLeftLength;
    private static double? _pendingRightLength;

    private static void RunDialogLoop(Document doc)
    {
        var db = doc.Database;
        var ed = doc.Editor;

        while (true)
        {
            IReadOnlyList<CrossAxisInfo> axes;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                // Popuni tabelu svim štapićima stacionaze (ako jos nisu registrovani).
                CrossAxisLayoutService.EnsureStationTicksRegistered(tr, db);
                axes = CrossAxisScanner.Scan(tr, db);
                tr.Commit();
            }

            CrossAxisPlacementSettings LoadSettings(long handle)
            {
                using var tr = db.TransactionManager.StartTransaction();
                var settings = CrossAxisStore.Load(tr, db, handle);
                tr.Commit();
                return settings;
            }

            IReadOnlyList<CrossAxisInfo> ReloadAxes()
            {
                using var tr = db.TransactionManager.StartTransaction();
                CrossAxisLayoutService.EnsureStationTicksRegistered(tr, db);
                var list = CrossAxisScanner.Scan(tr, db);
                tr.Commit();
                return list;
            }

            var initial = _pendingSettings
                ?? (axes.Count > 0 ? LoadSettings(axes[0].Handle) : new CrossAxisPlacementSettings());

            // null = selektuj sve; prazna lista = korisnik je poništio selekciju / nova iz crteža.
            var selection = _pendingSelection;
            var lengthsEnabled = _pendingLengthsEnabled;
            var leftLength = _pendingLeftLength;
            var rightLength = _pendingRightLength;
            _pendingSettings = null;
            _pendingSelection = null;
            _pendingLengthsEnabled = null;
            _pendingLeftLength = null;
            _pendingRightLength = null;

            var dialog = new CrossAxisSettingsDialog(
                axes,
                LoadSettings,
                ReloadAxes,
                initial,
                selection,
                lengthsEnabled,
                leftLength,
                rightLength);
            var accepted = AcApp.ShowModalWindow(dialog) == true;

            if (accepted && dialog.CloseAction == CrossAxisSettingsCloseAction.Confirmed)
            {
                ApplySettings(doc, dialog.Result);
                return;
            }

            switch (dialog.CloseAction)
            {
                case CrossAxisSettingsCloseAction.PickAxesInDrawing:
                    RememberPending(dialog.Result);
                    _pendingSelection = PickAndSelectAxes(doc) ?? dialog.Result.SelectedHandles;
                    continue;
                case CrossAxisSettingsCloseAction.PickLabelsOffset:
                    if (TryPickOffset(doc, dialog.Result, forLabels: true))
                    {
                        continue;
                    }

                    return;
                case CrossAxisSettingsCloseAction.PickStationsOffset:
                    if (TryPickOffset(doc, dialog.Result, forLabels: false))
                    {
                        continue;
                    }

                    return;
                default:
                    ed.WriteMessage("\nTCM-ROADS: komanda otkazana.");
                    return;
            }
        }
    }

    private static void RememberPending(CrossAxisSettingsDialogResult result)
    {
        _pendingSettings = result.Settings;
        _pendingSelection = result.SelectedHandles;
        _pendingLengthsEnabled = result.LengthsEnabled;
        _pendingLeftLength = result.LeftLength;
        _pendingRightLength = result.RightLength;
    }

    private static void ApplySettings(Document doc, CrossAxisSettingsDialogResult result)
    {
        using var tr = doc.Database.TransactionManager.StartTransaction();

        // Primena na ose u tabeli (selektovane / izabrane).
        var targetHandles = result.SelectedHandles.Count > 0
            ? result.SelectedHandles
            : result.AllAxes.Select(axis => axis.Handle).ToList();

        var updated = CrossAxisLayoutService.ApplyPlacement(
            tr,
            doc.Database,
            targetHandles,
            result.Settings);

        var resized = 0;
        if (result.LengthsEnabled)
        {
            StationFontPreferences.Load();
            StationFontPreferences.Save(
                StationFontPreferences.FontFileName,
                result.LeftLength,
                result.RightLength);
            // Posle placement — resize sa tačnim originom na putnoj osi.
            resized = CrossAxisLayoutService.ApplyLengthsToHandles(
                tr,
                doc.Database,
                targetHandles,
                result.LeftLength,
                result.RightLength);
        }

        tr.Commit();

        doc.Editor.WriteMessage(
            $"\nTCM-ROADS: Pomereno {updated} oznaka na {targetHandles.Count} poprecnih osa" +
            (result.LengthsEnabled ? $"; duzina L={result.LeftLength:0.####}/D={result.RightLength:0.####} ({resized} osa)." : "."));
    }

    private static IReadOnlyList<long>? PickAndSelectAxes(Document doc)
    {
        var ed = doc.Editor;
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nIzaberite objekte (selektovace se samo poprecne ose): "
        };
        // Bez filtera — korisnik moze Window/All; filtriramo na poprecne ose / RoleTick.
        var selection = ed.GetSelection(options);
        if (selection.Status != PromptStatus.OK)
        {
            return null;
        }

        using var tr = doc.Database.TransactionManager.StartTransaction();
        var candidateIds = new List<ObjectId>();
        var handles = new List<long>();
        var names = new List<string>();

        foreach (SelectedObject selected in selection.Value)
        {
            if (selected?.ObjectId.IsNull != false)
            {
                continue;
            }

            var entity = (Entity)tr.GetObject(selected.ObjectId, OpenMode.ForRead);
            var isCrossAxis = CrossAxisXData.TryReadCrossAxis(entity, out var number);
            var isTick = RoadXData.TryReadStationLabel(entity, out _, out var role, out _) &&
                         role == RoadXData.RoleTick;
            if (!isCrossAxis && !isTick)
            {
                continue;
            }

            candidateIds.Add(selected.ObjectId);
            if (isCrossAxis)
            {
                handles.Add(entity.Handle.Value);
                names.Add($"STA {number}");
            }
        }

        var registered = CrossAxisLayoutService.RegisterEntities(tr, doc.Database, candidateIds);

        // Posle registracije RoleTick → CX, ponovo sakupi handlove.
        handles.Clear();
        names.Clear();
        foreach (var id in candidateIds)
        {
            if (id.IsErased)
            {
                continue;
            }

            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (!CrossAxisXData.TryReadCrossAxis(entity, out var number))
            {
                continue;
            }

            handles.Add(entity.Handle.Value);
            names.Add($"STA {number}");
        }

        tr.Commit();

        if (handles.Count == 0)
        {
            ed.WriteMessage(
                "\nTCM-ROADS: U selekciji nema poprecnih osa (zanemareni su ostali objekti).");
            return Array.Empty<long>();
        }

        ed.WriteMessage(
            $"\nTCM-ROADS: U tabeli ({handles.Count}): {string.Join(", ", names)}" +
            (registered > 0 ? $" [novo: {registered}]." : "."));
        return handles;
    }

    private static bool TryPickOffset(Document doc, CrossAxisSettingsDialogResult pending, bool forLabels)
    {
        var ed = doc.Editor;
        if (pending.SelectedHandles.Count == 0)
        {
            return false;
        }

        Point3d origin;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (!CrossAxisScanner.TryGetEntity(tr, doc.Database, pending.SelectedHandles[0], out var axisEntity))
            {
                tr.Commit();
                ed.WriteMessage("\nTCM-ROADS: Poprecna osa nije pronadjena.");
                return false;
            }

            if (!CrossAxisGeometry.TryGetFrame(axisEntity, out origin, out _, out _))
            {
                tr.Commit();
                ed.WriteMessage("\nTCM-ROADS: Geometrija poprecne ose nije podrzana.");
                return false;
            }

            tr.Commit();
        }

        var pointOptions = new PromptPointOptions("\nOdredite polozaj oznake/stacionaze: ")
        {
            BasePoint = origin,
            UseBasePoint = true
        };
        var pointResult = ed.GetPoint(pointOptions);
        if (pointResult.Status != PromptStatus.OK)
        {
            return false;
        }

        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (!CrossAxisScanner.TryGetEntity(tr, doc.Database, pending.SelectedHandles[0], out var axisEntity) ||
                !CrossAxisLayoutService.TryComputeOffsetsFromPoint(axisEntity, pointResult.Value, out var offsetX, out var offsetY))
            {
                tr.Commit();
                ed.WriteMessage("\nTCM-ROADS: Nije moguce izracunati odmak.");
                return false;
            }

            tr.Commit();

            var settings = pending.Settings.Clone();
            if (forLabels)
            {
                settings.Labels.OffsetX = offsetX;
                settings.Labels.OffsetY = offsetY;
            }
            else
            {
                settings.Stations.OffsetX = offsetX;
                settings.Stations.OffsetY = offsetY;
            }

            _pendingSettings = settings;
            _pendingSelection = pending.SelectedHandles;
            _pendingLengthsEnabled = pending.LengthsEnabled;
            _pendingLeftLength = pending.LeftLength;
            _pendingRightLength = pending.RightLength;
        }

        return true;
    }
#endif
}
