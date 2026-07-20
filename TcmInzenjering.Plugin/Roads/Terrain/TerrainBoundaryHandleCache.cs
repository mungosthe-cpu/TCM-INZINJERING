using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Keš handle-ova granica po bazi — brzo prepoznavanje brisanja bez nove transakcije u ObjectErased.</summary>
internal static class TerrainBoundaryHandleCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, HashSet<long>> ByDb = new();

    public static void Set(Database db, IEnumerable<long> handles)
    {
        lock (Gate)
        {
            ByDb[db.UnmanagedObject] = handles.ToHashSet();
        }
    }

    public static void Refresh(Transaction tr, Database db) =>
        Set(db, LoadBoundariesSafe(tr, db));

    public static bool Contains(Database db, long handle)
    {
        lock (Gate)
        {
            return ByDb.TryGetValue(db.UnmanagedObject, out var set) && set.Contains(handle);
        }
    }

    private static IEnumerable<long> LoadBoundariesSafe(Transaction tr, Database db)
    {
        try
        {
            return TerrainDefinitionStore.LoadBoundaries(tr, db).Select(b => b.Handle);
        }
        catch
        {
            return Array.Empty<long>();
        }
    }
}
