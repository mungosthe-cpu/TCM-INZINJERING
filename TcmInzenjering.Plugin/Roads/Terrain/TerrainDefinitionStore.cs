using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal enum TerrainBoundaryKind : short
{
    Outer = 0,
    Hide = 1
}

internal readonly record struct TerrainBoundaryRef(long Handle, TerrainBoundaryKind Kind);

internal readonly record struct TerrainEdgeKey(Point3d A, Point3d B)
{
    public static TerrainEdgeKey Create(Point3d a, Point3d b)
    {
        // Stabilan kljuc u XY (+Z).
        if (a.X < b.X - 1e-9 || (Math.Abs(a.X - b.X) <= 1e-9 && a.Y < b.Y - 1e-9) ||
            (Math.Abs(a.X - b.X) <= 1e-9 && Math.Abs(a.Y - b.Y) <= 1e-9 && a.Z <= b.Z))
        {
            return new TerrainEdgeKey(a, b);
        }

        return new TerrainEdgeKey(b, a);
    }

    public bool Matches(Point3d p, Point3d q, double tol = 1e-6) =>
        (Near(A, p, tol) && Near(B, q, tol)) || (Near(A, q, tol) && Near(B, p, tol));

    private static bool Near(Point3d p, Point3d q, double tol) =>
        Math.Abs(p.X - q.X) <= tol && Math.Abs(p.Y - q.Y) <= tol && Math.Abs(p.Z - q.Z) <= tol;
}

/// <summary>
/// Definicija terena u NOD (pored tacaka): breakline, granice, TIN edit ops.
/// </summary>
internal static class TerrainDefinitionStore
{
    private const string DictionaryName = "TCM_TEREN";
    private const string BreaklinesKey = "BREAKLINES";
    private const string BoundariesKey = "BOUNDARIES";
    private const string DeletedEdgesKey = "DELETED_EDGES";
    private const string ForcedEdgesKey = "FORCED_EDGES";

    public static IReadOnlyList<long> LoadBreaklineHandles(Transaction tr, Database db) =>
        LoadHandleList(tr, db, BreaklinesKey);

    public static void SaveBreaklineHandles(Transaction tr, Database db, IReadOnlyList<long> handles) =>
        SaveHandleList(tr, db, BreaklinesKey, handles);

    public static IReadOnlyList<TerrainBoundaryRef> LoadBoundaries(Transaction tr, Database db)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        if (!dictionary.Contains(BoundariesKey))
        {
            return Array.Empty<TerrainBoundaryRef>();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(BoundariesKey), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length < 2)
        {
            return Array.Empty<TerrainBoundaryRef>();
        }

        var list = new List<TerrainBoundaryRef>();
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            if (!TryReadHandle(data[i], out var handle))
            {
                continue;
            }

            var kind = data[i + 1].TypeCode == (int)DxfCode.Int16
                ? (TerrainBoundaryKind)Convert.ToInt16(data[i + 1].Value)
                : TerrainBoundaryKind.Outer;
            list.Add(new TerrainBoundaryRef(handle, kind));
        }

        return list;
    }

    public static void SaveBoundaries(Transaction tr, Database db, IReadOnlyList<TerrainBoundaryRef> boundaries)
    {
        var buffer = new ResultBuffer();
        foreach (var b in boundaries)
        {
            buffer.Add(new TypedValue((int)DxfCode.Text, b.Handle.ToString()));
            buffer.Add(new TypedValue((int)DxfCode.Int16, (short)b.Kind));
        }

        WriteRecord(tr, db, BoundariesKey, buffer);
        TerrainBoundaryHandleCache.Set(db, boundaries.Select(b => b.Handle));
    }

    public static IReadOnlyList<TerrainEdgeKey> LoadDeletedEdges(Transaction tr, Database db) =>
        LoadEdges(tr, db, DeletedEdgesKey);

    public static IReadOnlyList<TerrainEdgeKey> LoadForcedEdges(Transaction tr, Database db) =>
        LoadEdges(tr, db, ForcedEdgesKey);

    public static void SaveDeletedEdges(Transaction tr, Database db, IReadOnlyList<TerrainEdgeKey> edges) =>
        SaveEdges(tr, db, DeletedEdgesKey, edges);

    public static void SaveForcedEdges(Transaction tr, Database db, IReadOnlyList<TerrainEdgeKey> edges) =>
        SaveEdges(tr, db, ForcedEdgesKey, edges);

    public static void AddDeletedEdge(Transaction tr, Database db, Point3d a, Point3d b) =>
        AddDeletedEdges(tr, db, new[] { (a, b) });

    /// <summary>
    /// Batch: jednom ucita i jednom snimi DELETED/FORCED Xrecord za sve ivice.
    /// </summary>
    public static void AddDeletedEdges(
        Transaction tr,
        Database db,
        IEnumerable<(Point3d A, Point3d B)> edges)
    {
        var incoming = edges?
            .Select(e => TerrainEdgeKey.Create(e.A, e.B))
            .ToList() ?? [];
        if (incoming.Count == 0)
        {
            return;
        }

        var deleted = LoadDeletedEdges(tr, db).ToList();
        var forced = LoadForcedEdges(tr, db).ToList();
        var deletedChanged = false;
        var forcedChanged = false;

        foreach (var key in incoming)
        {
            if (!deleted.Any(e => e.Matches(key.A, key.B)))
            {
                deleted.Add(key);
                deletedChanged = true;
            }

            var beforeForced = forced.Count;
            forced.RemoveAll(e => e.Matches(key.A, key.B));
            if (forced.Count != beforeForced)
            {
                forcedChanged = true;
            }
        }

        if (deletedChanged)
        {
            SaveDeletedEdges(tr, db, deleted);
        }

        if (forcedChanged)
        {
            SaveForcedEdges(tr, db, forced);
        }
    }

    public static void AddForcedEdge(Transaction tr, Database db, Point3d a, Point3d b)
        => AddForcedEdges(tr, db, new[] { (a, b) });

    /// <summary>Batch: jednom učita i upiše FORCED/DELETED za sve nove ivice.</summary>
    public static void AddForcedEdges(
        Transaction tr,
        Database db,
        IEnumerable<(Point3d A, Point3d B)> edges)
    {
        var forced = LoadForcedEdges(tr, db).ToList();
        var deleted = LoadDeletedEdges(tr, db).ToList();
        var forcedChanged = false;
        var deletedChanged = false;

        foreach (var (a, b) in edges)
        {
            var key = TerrainEdgeKey.Create(a, b);
            if (!forced.Any(e => e.Matches(key.A, key.B)))
            {
                forced.Add(key);
                forcedChanged = true;
            }

            var before = deleted.Count;
            deleted.RemoveAll(e => e.Matches(a, b));
            deletedChanged |= deleted.Count != before;
        }

        if (forcedChanged)
        {
            SaveForcedEdges(tr, db, forced);
        }

        if (deletedChanged)
        {
            SaveDeletedEdges(tr, db, deleted);
        }
    }

    public static void AddForcedEdgeAfterSwap(Transaction tr, Database db, Point3d oldA, Point3d oldB, Point3d newC, Point3d newD)
    {
        var forced = LoadForcedEdges(tr, db).ToList();
        forced.RemoveAll(e => e.Matches(oldA, oldB));
        var neu = TerrainEdgeKey.Create(newC, newD);
        if (!forced.Any(e => e.Matches(neu.A, neu.B)))
        {
            forced.Add(neu);
        }

        SaveForcedEdges(tr, db, forced);

        var deleted = LoadDeletedEdges(tr, db).ToList();
        var oldKey = TerrainEdgeKey.Create(oldA, oldB);
        if (!deleted.Any(e => e.Matches(oldKey.A, oldKey.B)))
        {
            deleted.Add(oldKey);
        }

        deleted.RemoveAll(e => e.Matches(newC, newD));
        SaveDeletedEdges(tr, db, deleted);
    }

    public static void ClearEditOps(Transaction tr, Database db)
    {
        SaveDeletedEdges(tr, db, Array.Empty<TerrainEdgeKey>());
        SaveForcedEdges(tr, db, Array.Empty<TerrainEdgeKey>());
    }

    private static IReadOnlyList<long> LoadHandleList(Transaction tr, Database db, string key)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        if (!dictionary.Contains(key))
        {
            return Array.Empty<long>();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null)
        {
            return Array.Empty<long>();
        }

        var list = new List<long>();
        foreach (var v in data)
        {
            if (TryReadHandle(v, out var h))
            {
                list.Add(h);
            }
        }

        return list.Distinct().ToList();
    }

    private static void SaveHandleList(Transaction tr, Database db, string key, IReadOnlyList<long> handles)
    {
        var buffer = new ResultBuffer();
        foreach (var h in handles.Distinct())
        {
            buffer.Add(new TypedValue((int)DxfCode.Text, h.ToString()));
        }

        WriteRecord(tr, db, key, buffer);
    }

    private static bool TryReadHandle(TypedValue v, out long handle)
    {
        handle = 0;
        try
        {
            if (v.TypeCode == (int)DxfCode.Text || v.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
            {
                return long.TryParse(Convert.ToString(v.Value), out handle);
            }

            if (v.TypeCode is (int)DxfCode.Int32 or (int)DxfCode.Int16 or (int)DxfCode.Int64)
            {
                handle = Convert.ToInt64(v.Value);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static List<TerrainEdgeKey> LoadEdges(Transaction tr, Database db, string key)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        if (!dictionary.Contains(key))
        {
            return [];
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length < 6)
        {
            return [];
        }

        var list = new List<TerrainEdgeKey>();
        for (var i = 0; i + 5 < data.Length; i += 6)
        {
            if (data[i].TypeCode != (int)DxfCode.Real)
            {
                continue;
            }

            var a = new Point3d(
                Convert.ToDouble(data[i].Value),
                Convert.ToDouble(data[i + 1].Value),
                Convert.ToDouble(data[i + 2].Value));
            var b = new Point3d(
                Convert.ToDouble(data[i + 3].Value),
                Convert.ToDouble(data[i + 4].Value),
                Convert.ToDouble(data[i + 5].Value));
            list.Add(TerrainEdgeKey.Create(a, b));
        }

        return list;
    }

    private static void SaveEdges(Transaction tr, Database db, string key, IReadOnlyList<TerrainEdgeKey> edges)
    {
        var buffer = new ResultBuffer();
        foreach (var e in edges)
        {
            buffer.Add(new TypedValue((int)DxfCode.Real, e.A.X));
            buffer.Add(new TypedValue((int)DxfCode.Real, e.A.Y));
            buffer.Add(new TypedValue((int)DxfCode.Real, e.A.Z));
            buffer.Add(new TypedValue((int)DxfCode.Real, e.B.X));
            buffer.Add(new TypedValue((int)DxfCode.Real, e.B.Y));
            buffer.Add(new TypedValue((int)DxfCode.Real, e.B.Z));
        }

        WriteRecord(tr, db, key, buffer);
    }

    private static void WriteRecord(Transaction tr, Database db, string key, ResultBuffer buffer)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        if (dictionary.Contains(key))
        {
            var existing = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        var record = new Xrecord { Data = buffer };
        dictionary.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static DBDictionary GetDictionary(Transaction tr, Database db, OpenMode mode)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            if (mode != OpenMode.ForWrite)
            {
                return new DBDictionary();
            }

            nod.UpgradeOpen();
            var dictionary = new DBDictionary();
            nod.SetAt(DictionaryName, dictionary);
            tr.AddNewlyCreatedDBObject(dictionary, true);
            return dictionary;
        }

        return (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), mode);
    }
}
