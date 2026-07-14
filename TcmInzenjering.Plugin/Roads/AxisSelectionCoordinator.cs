using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisSelectionCoordinator
{
    private static bool _reselecting;
    private static bool _idleHooked;
    private static Document? _pendingDoc;
    private static readonly HashSet<ObjectId> PendingPolylineIds = new();
    private static readonly HashSet<ObjectId> PendingAxisIdsToRemove = new();

    public static void Initialize()
    {
        AcApp.DocumentManager.DocumentCreated += OnDocumentCreated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            AttachDocument(doc);
        }
    }

    public static void Terminate()
    {
        AcApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            DetachDocument(doc);
        }
    }

    private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
    {
        AttachDocument(e.Document);
    }

    private static void AttachDocument(Document doc)
    {
        DetachDocument(doc);
        doc.Editor.SelectionAdded += OnSelectionAdded;
    }

    private static void DetachDocument(Document doc)
    {
        doc.Editor.SelectionAdded -= OnSelectionAdded;
    }

    private static void OnSelectionAdded(object sender, SelectionAddedEventArgs e)
    {
        if (_reselecting)
        {
            return;
        }

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var axisIdsToRemove = new HashSet<ObjectId>();
        var polylineIdsToAdd = new HashSet<ObjectId>();

        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (SelectedObject selected in e.Selection)
            {
                if (selected.ObjectId.IsNull || selected.ObjectId.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(selected.ObjectId, OpenMode.ForRead) is not Entity entity)
                {
                    continue;
                }

                if (RoadXData.TryReadSourcePolyline(entity, out _))
                {
                    continue;
                }

                if (!RoadXData.TryReadAxisElement(entity, out var axisName, out _))
                {
                    continue;
                }

                var metadata = RoadAxisStore.Load(tr, doc.Database, axisName);
                if (metadata is null ||
                    !metadata.HasSourcePolyline ||
                    !AxisPolylineResolver.TryResolve(doc.Database, metadata.SourcePolylineHandle, out var polylineId))
                {
                    continue;
                }

                axisIdsToRemove.Add(selected.ObjectId);
                polylineIdsToAdd.Add(polylineId);
            }
        }

        if (axisIdsToRemove.Count == 0)
        {
            return;
        }

        foreach (var id in axisIdsToRemove)
        {
            PendingAxisIdsToRemove.Add(id);
        }

        foreach (var id in polylineIdsToAdd)
        {
            PendingPolylineIds.Add(id);
        }

        _pendingDoc = doc;
        EnsureIdleHooked();
    }

    private static void EnsureIdleHooked()
    {
        if (_idleHooked)
        {
            return;
        }

        _idleHooked = true;
        AcApp.Idle += OnIdleReselect;
    }

    private static void OnIdleReselect(object? sender, EventArgs e)
    {
        AcApp.Idle -= OnIdleReselect;
        _idleHooked = false;

        var doc = _pendingDoc;
        var axisIdsToRemove = PendingAxisIdsToRemove.ToArray();
        var polylineIdsToAdd = PendingPolylineIds.ToArray();
        _pendingDoc = null;
        PendingAxisIdsToRemove.Clear();
        PendingPolylineIds.Clear();

        if (doc is null)
        {
            return;
        }

        try
        {
            _reselecting = true;
            var editor = doc.Editor;
            var selection = new HashSet<ObjectId>();
            var implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK)
            {
                foreach (SelectedObject selected in implied.Value)
                {
                    if (!axisIdsToRemove.Contains(selected.ObjectId))
                    {
                        selection.Add(selected.ObjectId);
                    }
                }
            }

            foreach (var polylineId in polylineIdsToAdd)
            {
                selection.Add(polylineId);
            }

            editor.SetImpliedSelection(selection.ToArray());
        }
        finally
        {
            _reselecting = false;
        }
    }

    private static void ClearPending()
    {
        _pendingDoc = null;
        PendingAxisIdsToRemove.Clear();
        PendingPolylineIds.Clear();
    }
}
