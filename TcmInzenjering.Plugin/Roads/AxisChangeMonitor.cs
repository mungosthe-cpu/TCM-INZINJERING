using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisChangeMonitor
{
    private static readonly HashSet<string> DirtyAxes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _idleHooked;

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
        doc.Database.ObjectModified += OnObjectModified;
        doc.CommandEnded += OnCommandEnded;
    }

    private static void DetachDocument(Document doc)
    {
        doc.Database.ObjectModified -= OnObjectModified;
        doc.CommandEnded -= OnCommandEnded;
    }

    private static void OnObjectModified(object sender, ObjectEventArgs e)
    {
        if (e.DBObject is not Entity entity || !RoadXData.TryReadAxisElement(entity, out var axisName, out _))
        {
            return;
        }

        lock (DirtyAxes)
        {
            DirtyAxes.Add(axisName);
        }
    }

    private static void OnCommandEnded(object sender, CommandEventArgs e)
    {
        if (IsOwnCommand(e.GlobalCommandName))
        {
            lock (DirtyAxes)
            {
                DirtyAxes.Clear();
            }

            return;
        }

        string[] axesToUpdate;
        lock (DirtyAxes)
        {
            if (DirtyAxes.Count == 0)
            {
                return;
            }

            axesToUpdate = DirtyAxes.ToArray();
            DirtyAxes.Clear();
        }

        if (!_idleHooked)
        {
            _idleHooked = true;
            AcApp.Idle += OnIdleUpdate;
        }

        PendingAxes = axesToUpdate;
    }

    private static string[] PendingAxes = Array.Empty<string>();

    private static void OnIdleUpdate(object? sender, EventArgs e)
    {
        if (PendingAxes.Length == 0)
        {
            _idleHooked = false;
            AcApp.Idle -= OnIdleUpdate;
            return;
        }

        var axes = PendingAxes;
        PendingAxes = Array.Empty<string>();
        _idleHooked = false;
        AcApp.Idle -= OnIdleUpdate;

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        try
        {
            using var docLock = doc.LockDocument();
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var updated = 0;
            foreach (var axisName in axes)
            {
                updated += StationLabelService.RefreshAxis(tr, doc.Database, axisName);
            }

            tr.Commit();

            if (updated > 0)
            {
                doc.Editor.WriteMessage($"\nTCM-INZINJERING: Azurirana geometrija osovine i {updated} oznaka stacionaze.");
            }
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\nTCM-INZINJERING: greska pri azuriranju stacionaza - {ex.Message}");
        }
    }

    private static bool IsOwnCommand(string commandName)
    {
        return commandName.StartsWith("TCM", StringComparison.OrdinalIgnoreCase);
    }
}
