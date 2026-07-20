using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Brisanje polilinije granice: uklanja iz crteža/TIN-a, ostavlja u projektu (sa !),
/// snima geometriju za kasnije vraćanje.
/// </summary>
internal static class TerrainBoundaryEraseMonitor
{
    private sealed class PendingCapture
    {
        public required List<Point3d> Points { get; init; }
        public required bool Closed { get; init; }
        public TerrainBoundaryKind? KindFromXData { get; init; }
        public string? SurfaceName { get; init; }
    }

    private static readonly HashSet<long> PendingHandles = new();
    private static readonly Dictionary<long, PendingCapture> PendingCaptures = new();
    private static bool _idleHooked;
    private static int _suppress;

    public static void Initialize()
    {
        AcApp.DocumentManager.DocumentCreated += OnDocumentCreated;
        AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            Attach(doc);
            RefreshCache(doc);
        }
    }

    public static void Terminate()
    {
        AcApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
        AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            Detach(doc);
        }
    }

    public static void Suppress() => _suppress++;

    public static void Resume()
    {
        if (_suppress > 0)
        {
            _suppress--;
        }
    }

    private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
    {
        Attach(e.Document);
        RefreshCache(e.Document);
    }

    private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e) =>
        RefreshCache(e.Document);

    private static void RefreshCache(Document? doc)
    {
        if (doc?.Database is null)
        {
            return;
        }

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            TerrainBoundaryHandleCache.Refresh(tr, doc.Database);
            tr.Commit();
        }
        catch
        {
            // ignore
        }
    }

    private static void Attach(Document doc)
    {
        Detach(doc);
        doc.Database.ObjectErased += OnObjectErased;
        doc.CommandEnded += OnCommandEnded;
        doc.CommandCancelled += OnCommandEnded;
        doc.CommandFailed += OnCommandEnded;
    }

    private static void Detach(Document doc)
    {
        doc.Database.ObjectErased -= OnObjectErased;
        doc.CommandEnded -= OnCommandEnded;
        doc.CommandCancelled -= OnCommandEnded;
        doc.CommandFailed -= OnCommandEnded;
    }

    private static void OnObjectErased(object sender, ObjectErasedEventArgs e)
    {
        if (_suppress > 0 || e.DBObject is not Entity entity)
        {
            return;
        }

        var db = entity.Database;
        var handle = entity.Handle.Value;
        var isBound =
            TerrainUserBoundaryXData.IsUserBoundary(entity) ||
            string.Equals(entity.Layer, TerrainUserBoundaryXData.LayerName, StringComparison.OrdinalIgnoreCase) ||
            TerrainBoundaryHandleCache.Contains(db, handle);

        if (!isBound)
        {
            return;
        }

        if (TerrainBoundarySnapshotStore.TryCaptureEntity(entity, out var points, out var closed))
        {
            TerrainBoundaryKind? kind = null;
            string? surface = null;
            if (TerrainUserBoundaryXData.TryRead(entity, out var k, out var s))
            {
                kind = k;
                surface = s;
            }

            lock (PendingHandles)
            {
                PendingCaptures[handle] = new PendingCapture
                {
                    Points = points.ToList(),
                    Closed = closed,
                    KindFromXData = kind,
                    SurfaceName = surface
                };
            }
        }

        lock (PendingHandles)
        {
            PendingHandles.Add(handle);
        }

        EnsureIdle();
    }

    private static void OnCommandEnded(object sender, CommandEventArgs e)
    {
        if (_suppress > 0)
        {
            return;
        }

        EnsureIdle();
    }

    private static void EnsureIdle()
    {
        if (_idleHooked)
        {
            return;
        }

        _idleHooked = true;
        AcApp.Idle += OnIdle;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        long[] handles;
        Dictionary<long, PendingCapture> captures;
        lock (PendingHandles)
        {
            AcApp.Idle -= OnIdle;
            _idleHooked = false;
            if (PendingHandles.Count == 0)
            {
                return;
            }

            handles = PendingHandles.ToArray();
            captures = PendingCaptures
                .Where(kv => PendingHandles.Contains(kv.Key) || handles.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var h in handles)
            {
                PendingCaptures.Remove(h);
            }

            PendingHandles.Clear();
        }

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        try
        {
            _suppress++;
            using var docLock = doc.LockDocument();
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var list = TerrainDefinitionStore.LoadBoundaries(tr, doc.Database).ToList();
            var removed = list.Where(b => handles.Contains(b.Handle)).ToList();
            if (removed.Count == 0 && captures.Count == 0)
            {
                // Handle bio u kešu/XData ali već skinut iz store-a — i dalje snimi snapshot ako ima capture.
                foreach (var kv in captures)
                {
                    var handle = kv.Key;
                    var cap = kv.Value;
                    var kind = cap.KindFromXData ?? TerrainBoundaryKind.Outer;
                    var key = TcmProjectStore.FormatBoundaryKey(kind, handle);
                    var surface = cap.SurfaceName
                                  ?? NamedTerrainSurfaceStore.GetActiveName(tr, doc.Database)
                                  ?? "Teren_1";
                    TerrainBoundarySnapshotStore.Upsert(
                        tr,
                        doc.Database,
                        TerrainBoundarySnapshot.From(key, kind, surface, cap.Closed, cap.Points));
                }

                tr.Commit();
                return;
            }

            list.RemoveAll(b => handles.Contains(b.Handle));
            TerrainDefinitionStore.SaveBoundaries(tr, doc.Database, list);

            var active = NamedTerrainSurfaceStore.GetActiveName(tr, doc.Database) ?? "Teren_1";
            if (NamedTerrainSurfaceStore.IsBoundaryCompanionName(active))
            {
                active = active[..^("_Granica".Length)];
                if (string.IsNullOrWhiteSpace(active))
                {
                    active = "Teren_1";
                }
            }

            foreach (var b in removed)
            {
                captures.TryGetValue(b.Handle, out var cap);
                if (cap is null)
                {
                    continue;
                }

                var key = TcmProjectStore.FormatBoundaryKey(b.Kind, b.Handle);
                var surface = cap.SurfaceName ?? active;
                TerrainBoundarySnapshotStore.Upsert(
                    tr,
                    doc.Database,
                    TerrainBoundarySnapshot.From(key, b.Kind, surface, cap.Closed, cap.Points));
            }

            // Capture bez stavke u listi (npr. samo lejer) — snimi po XData kind.
            foreach (var kv in captures)
            {
                var handle = kv.Key;
                var cap = kv.Value;
                if (removed.Any(r => r.Handle == handle))
                {
                    continue;
                }

                var kind = cap.KindFromXData ?? TerrainBoundaryKind.Outer;
                var key = TcmProjectStore.FormatBoundaryKey(kind, handle);
                TerrainBoundarySnapshotStore.Upsert(
                    tr,
                    doc.Database,
                    TerrainBoundarySnapshot.From(key, kind, cap.SurfaceName ?? active, cap.Closed, cap.Points));
            }

            var granicaName = NamedTerrainSurfaceStore.BoundaryCompanionName(active);
            NamedTerrainSurfaceStore.DeleteSurface(tr, doc.Database, granicaName);

            var ms = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),
                OpenMode.ForWrite);
            TerrainBoundaryPointDrawer.Sync(tr, doc.Database, ms, granicaName, Array.Empty<Point3d>());
            tr.Commit();

            if (removed.Count > 0 || captures.Count > 0)
            {
                RoadCommands.RebuildTerrainFacesPublic(doc.Editor, doc.Database, announce: true, active);
            }

            // Projekat: NE briši — ostaje sa "!" dok korisnik ne vrati ili ne ukloni ručno.
            doc.Editor.WriteMessage(
                "\nTCM-ROADS: Granica uklonjena iz crteza — TIN bez granice. " +
                "U projektu ostaje sa ! ; dupli klik = vrati granicu.");
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\nTCM-ROADS: greska pri uklanjanju granice — {ex.Message}");
        }
        finally
        {
            _suppress--;
        }
    }
}
