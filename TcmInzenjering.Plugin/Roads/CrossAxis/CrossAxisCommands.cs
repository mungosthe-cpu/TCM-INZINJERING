using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if NET8_0_OR_GREATER
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

#if NET48
        doc.Editor.WriteMessage("\nTCM-INZINJERING: Podesavanje poprecnih osa je dostupno u AutoCAD 2025+ verziji plugina.");
        return;
#else
        RunDialogLoop(doc);
#endif
    }

#if NET8_0_OR_GREATER
    private static CrossAxisPlacementSettings? _pendingSettings;
    private static IReadOnlyList<long>? _pendingSelection;

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
            _pendingSettings = null;
            _pendingSelection = null;

            var dialog = new CrossAxisSettingsDialog(axes, LoadSettings, ReloadAxes, initial, selection);
            var accepted = AcApp.ShowModalWindow(dialog) == true;

            if (accepted && dialog.CloseAction == CrossAxisSettingsCloseAction.Confirmed)
            {
                ApplySettings(doc, dialog.Result);
                return;
            }

            switch (dialog.CloseAction)
            {
                case CrossAxisSettingsCloseAction.PickAxesInDrawing:
                    _pendingSettings = dialog.Result.Settings;
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
                    ed.WriteMessage("\nTCM-INZINJERING: komanda otkazana.");
                    return;
            }
        }
    }

    private static void ApplySettings(Document doc, CrossAxisSettingsDialogResult result)
    {
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var updated = CrossAxisLayoutService.ApplyPlacement(
            tr,
            doc.Database,
            result.SelectedHandles,
            result.Settings);
        tr.Commit();

        doc.Editor.WriteMessage(
            updated > 0
                ? $"\nTCM-INZINJERING: Pomereno {updated} oznaka na {result.SelectedHandles.Count} poprecnih osa."
                : $"\nTCM-INZINJERING: Nije pomerena nijedna oznaka. Proveri da li oko ose postoji tekst stacionaze (STA … / 0-xxx.xx).");
    }

    private static IReadOnlyList<long>? PickAndSelectAxes(Document doc)
    {
        var ed = doc.Editor;
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nIzaberite poprecne ose (linije/polilinije ili stapiće stacionaze): "
        };
        var filter = new SelectionFilter(
        [
            new TypedValue((int)DxfCode.Start, "LINE,POLYLINE,LWPOLYLINE")
        ]);
        var selection = ed.GetSelection(options, filter);
        if (selection.Status != PromptStatus.OK)
        {
            return null;
        }

        using var tr = doc.Database.TransactionManager.StartTransaction();
        var registered = CrossAxisLayoutService.RegisterEntities(
            tr,
            doc.Database,
            selection.Value.GetObjectIds());

        var handles = new List<long>();
        var names = new List<string>();
        foreach (SelectedObject selected in selection.Value)
        {
            if (selected?.ObjectId.IsNull != false)
            {
                continue;
            }

            var entity = (Entity)tr.GetObject(selected.ObjectId, OpenMode.ForRead);
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
                "\nTCM-INZINJERING: Nista nije registrovano. Izaberite LINIJU poprecne ose ili stapic stacionaze (ne tekst).");
            return Array.Empty<long>();
        }

        ed.WriteMessage(
            $"\nTCM-INZINJERING: U tabeli ({handles.Count}): {string.Join(", ", names)}" +
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
                ed.WriteMessage("\nTCM-INZINJERING: Poprecna osa nije pronadjena.");
                return false;
            }

            if (!CrossAxisGeometry.TryGetFrame(axisEntity, out origin, out _, out _))
            {
                tr.Commit();
                ed.WriteMessage("\nTCM-INZINJERING: Geometrija poprecne ose nije podrzana.");
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
                ed.WriteMessage("\nTCM-INZINJERING: Nije moguce izracunati odmak.");
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
        }

        return true;
    }
#endif
}
