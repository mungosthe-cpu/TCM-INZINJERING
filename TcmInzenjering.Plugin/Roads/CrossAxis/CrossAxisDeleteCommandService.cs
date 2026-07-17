#if !BRICSCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.CrossAxis;
using TcmInzenjering.Plugin.Roads.Profile;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

internal static class CrossAxisDeleteCommandService
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

        while (true)
        {
            IReadOnlyList<CrossAxisInfo> axes;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CrossAxisLayoutService.EnsureStationTicksRegistered(tr, db);
                axes = CrossAxisScanner.Scan(tr, db);
                tr.Commit();
            }

            var dialog = new CrossAxisDeleteDialog(axes);
            var accepted = AcApp.ShowModalWindow(dialog) == true;

            if (!accepted || dialog.CloseAction == CrossAxisDeleteCloseAction.Closed)
            {
                return;
            }

            if (dialog.CloseAction == CrossAxisDeleteCloseAction.Refresh)
            {
                continue;
            }

            if (dialog.CloseAction == CrossAxisDeleteCloseAction.PickInDrawing)
            {
                var picked = PickAxes(doc);
                if (picked is null || picked.Count == 0)
                {
                    continue;
                }

                ApplyDelete(doc, picked);
                continue;
            }

            if (dialog.CloseAction == CrossAxisDeleteCloseAction.DeleteSelected)
            {
                ApplyDelete(doc, dialog.SelectedHandles);
            }
        }
    }

    private static void ApplyDelete(Document doc, IReadOnlyList<long> handles)
    {
        if (handles.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> affected;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            affected = CrossAxisDrawService.DeleteByHandles(tr, doc.Database, handles);
            tr.Commit();
        }

        foreach (var axisName in affected)
        {
            ProfileViewRefresh.ScheduleIfExists(doc, axisName);
        }

        doc.Editor.WriteMessage(
            $"\nTCM-INZINJERING: Obrisano {handles.Count} poprecnih osa" +
            (affected.Count > 0 ? $" (osovine: {string.Join(", ", affected)})." : "."));
        doc.Editor.Regen();
    }

    private static IReadOnlyList<long>? PickAxes(Document doc)
    {
        var ed = doc.Editor;
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nIzaberite poprečne ose za brisanje (Enter = kraj): "
        };

        var selection = ed.GetSelection(options);
        if (selection.Status != PromptStatus.OK)
        {
            return null;
        }

        // Ne zovi RegisterEntities — pretvara RoleText/CHNG u CXLB/CXST i onda brisanje
        // ne nađe natpise po stacionaži.
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var handles = new List<long>();
        foreach (SelectedObject selected in selection.Value)
        {
            if (selected?.ObjectId.IsNull != false || selected.ObjectId.IsErased)
            {
                continue;
            }

            var entity = (Entity)tr.GetObject(selected.ObjectId, OpenMode.ForRead);
            if (CrossAxisXData.TryReadCrossAxis(entity, out _))
            {
                handles.Add(entity.Handle.Value);
                continue;
            }

            // RoleTick bez CAXIS — i dalje je poprečna osa na situaciji.
            if (RoadXData.TryReadStationLabel(entity, out _, out var role, out _) &&
                role == RoadXData.RoleTick)
            {
                handles.Add(entity.Handle.Value);
            }
        }

        tr.Commit();

        if (handles.Count == 0)
        {
            ed.WriteMessage("\nTCM-INZINJERING: U selekciji nema poprečnih osa.");
            return Array.Empty<long>();
        }

        return handles.Distinct().ToList();
    }
}
#endif
