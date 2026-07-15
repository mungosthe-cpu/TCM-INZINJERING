using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisChangeMonitor
{
    private static readonly HashSet<string> PolylineDirtyAxes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AxisGeometryDirtyAxes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _idleHooked;
    private static int _suppressModifiedDepth;

    private static string[] PendingPolylineAxes = Array.Empty<string>();
    private static string[] PendingAxisGeometryAxes = Array.Empty<string>();

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
        doc.Database.ObjectModified += OnObjectModified;
        doc.CommandEnded += OnCommandEnded;
        doc.CommandCancelled += OnCommandEnded;
        doc.CommandFailed += OnCommandEnded;
        SeedAxisReferences(doc);
    }

    private static void DetachDocument(Document doc)
    {
        doc.Database.ObjectModified -= OnObjectModified;
        doc.CommandEnded -= OnCommandEnded;
        doc.CommandCancelled -= OnCommandEnded;
        doc.CommandFailed -= OnCommandEnded;
    }

    private static void OnObjectModified(object sender, ObjectEventArgs e)
    {
        if (_suppressModifiedDepth > 0)
        {
            return;
        }

        if (e.DBObject is not Entity entity)
        {
            return;
        }

        if (RoadXData.TryReadSourcePolyline(entity, out var sourceAxisName))
        {
            lock (PolylineDirtyAxes)
            {
                PolylineDirtyAxes.Add(sourceAxisName);
            }

            EnsureIdleHooked();
            return;
        }

        if (!RoadXData.TryReadAxisElement(entity, out var axisName, out _))
        {
            return;
        }

        lock (AxisGeometryDirtyAxes)
        {
            AxisGeometryDirtyAxes.Add(axisName);
        }

        EnsureIdleHooked();
    }

    private static void OnCommandEnded(object sender, CommandEventArgs e)
    {
        if (IsOwnCommand(e.GlobalCommandName))
        {
            lock (PolylineDirtyAxes)
            {
                PolylineDirtyAxes.Clear();
            }

            lock (AxisGeometryDirtyAxes)
            {
                AxisGeometryDirtyAxes.Clear();
            }

            PendingPolylineAxes = Array.Empty<string>();
            PendingAxisGeometryAxes = Array.Empty<string>();
            return;
        }

        DrainDirtyAxesToPending();
        EnsureIdleHooked();
    }

    private static string[] MergeAxisNames(string[] existing, string[] incoming) =>
        existing
            .Concat(incoming)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void OnIdleUpdate(object? sender, EventArgs e)
    {
        DrainDirtyAxesToPending();
        if (PendingPolylineAxes.Length == 0 && PendingAxisGeometryAxes.Length == 0)
        {
            _idleHooked = false;
            AcApp.Idle -= OnIdleUpdate;
            return;
        }

        var dirtyAxes = MergeAxisNames(PendingPolylineAxes, PendingAxisGeometryAxes);
        PendingPolylineAxes = Array.Empty<string>();
        PendingAxisGeometryAxes = Array.Empty<string>();
        _idleHooked = false;
        AcApp.Idle -= OnIdleUpdate;

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        try
        {
            _suppressModifiedDepth++;
            using var docLock = doc.LockDocument();
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var updated = 0;
            foreach (var axisName in dirtyAxes)
            {
                var metadata = RoadAxisStore.Load(tr, doc.Database, axisName);
                if (metadata?.HasSourcePolyline == true)
                {
                    updated += StationLabelService.RefreshAxisFromPolyline(tr, doc.Database, axisName);
                    continue;
                }

                updated += StationLabelService.RefreshAxisGeometry(tr, doc.Database, axisName);
            }

            tr.Commit();

            if (updated > 0)
            {
                doc.Editor.WriteMessage(
                    $"\nTCM-INZINJERING: Azurirana geometrija osovine, oznake i 3D projekcija (ako postoji) — {updated} elemenata.");
            }
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\nTCM-INZINJERING: greska pri azuriranju stacionaza - {ex.Message}");
        }
        finally
        {
            _suppressModifiedDepth--;
        }
    }

    private static bool IsOwnCommand(string commandName)
    {
        return commandName.StartsWith("TCM", StringComparison.OrdinalIgnoreCase);
    }

    private static void DrainDirtyAxesToPending()
    {
        string[] polylineAxes;
        string[] axisGeometryAxes;
        lock (PolylineDirtyAxes)
        lock (AxisGeometryDirtyAxes)
        {
            if (PolylineDirtyAxes.Count == 0 && AxisGeometryDirtyAxes.Count == 0)
            {
                return;
            }

            polylineAxes = PolylineDirtyAxes.ToArray();
            axisGeometryAxes = AxisGeometryDirtyAxes.ToArray();
            PolylineDirtyAxes.Clear();
            AxisGeometryDirtyAxes.Clear();
        }

        PendingPolylineAxes = MergeAxisNames(PendingPolylineAxes, polylineAxes);
        PendingAxisGeometryAxes = MergeAxisNames(PendingAxisGeometryAxes, axisGeometryAxes);
    }

    private static void EnsureIdleHooked()
    {
        if (_idleHooked)
        {
            return;
        }

        _idleHooked = true;
        AcApp.Idle += OnIdleUpdate;
    }

    private static void SeedAxisReferences(Document doc)
    {
        try
        {
            using var docLock = doc.LockDocument();
            using var tr = doc.Database.TransactionManager.StartTransaction();
            RoadDrawing.EnsureAxisLayerPickThrough(tr, doc.Database);
            RoadDrawing.EnsureProjectedAxisLayerPickThrough(tr, doc.Database);
            foreach (var axisName in RoadAxisStore.GetAxisNames(tr, doc.Database))
            {
                var metadata = RoadAxisStore.Load(tr, doc.Database, axisName);
                if (metadata is null)
                {
                    continue;
                }

                var axis = AxisGeometryReader.ReadAxis(tr, doc.Database, axisName, metadata.StartStation);
                if (axis is null || axis.Elements.Count == 0)
                {
                    continue;
                }

                AxisReferenceTracker.Update(axisName, axis.Elements[0].Start);
                RoadDrawing.EnsureTangentOnTop(tr, doc.Database, axisName);
            }

            tr.Commit();
        }
        catch
        {
            // Dokument moze biti jos u fazi ucitavanja.
        }
    }
}
