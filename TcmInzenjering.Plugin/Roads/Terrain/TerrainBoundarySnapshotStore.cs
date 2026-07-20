using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Snimak obrisane granice — omogućava vraćanje iz projekta.</summary>
internal sealed class TerrainBoundarySnapshot
{
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = nameof(TerrainBoundaryKind.Outer);
    public string SurfaceName { get; set; } = "Teren_1";
    public bool Closed { get; set; } = true;
    public List<double[]> Points { get; set; } = [];

    public TerrainBoundaryKind ParsedKind =>
        Enum.TryParse(Kind, ignoreCase: true, out TerrainBoundaryKind k)
            ? k
            : TerrainBoundaryKind.Outer;

    public IReadOnlyList<Point3d> ToPoints()
    {
        var list = new List<Point3d>(Points.Count);
        foreach (var p in Points)
        {
            if (p is { Length: >= 2 })
            {
                list.Add(new Point3d(p[0], p[1], p.Length >= 3 ? p[2] : 0));
            }
        }

        return list;
    }

    public static TerrainBoundarySnapshot From(
        string key,
        TerrainBoundaryKind kind,
        string? surfaceName,
        bool closed,
        IReadOnlyList<Point3d> points) =>
        new()
        {
            Key = key,
            Kind = kind.ToString(),
            SurfaceName = string.IsNullOrWhiteSpace(surfaceName) ? "Teren_1" : surfaceName.Trim(),
            Closed = closed,
            Points = points.Select(p => new[] { p.X, p.Y, p.Z }).ToList()
        };
}

/// <summary>Snapshot-ovi granica u NOD crteža (TCM_TEREN / BOUNDARY_SNAPSHOTS).</summary>
internal static class TerrainBoundarySnapshotStore
{
    private const string DictionaryName = "TCM_TEREN";
    private const string SnapshotsKey = "BOUNDARY_SNAPSHOTS";

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<TerrainBoundarySnapshot> LoadAll(Transaction tr, Database db)
    {
        try
        {
            var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
            if (dictionary is null || !dictionary.Contains(SnapshotsKey))
            {
                return [];
            }

            var record = (Xrecord)tr.GetObject(dictionary.GetAt(SnapshotsKey), OpenMode.ForRead);
            var json = Convert.ToString(record.Data?.AsArray()?.FirstOrDefault().Value);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<TerrainBoundarySnapshot>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static TerrainBoundarySnapshot? Find(Transaction tr, Database db, string key) =>
        LoadAll(tr, db).FirstOrDefault(s =>
            string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool Has(Transaction tr, Database db, string key) =>
        Find(tr, db, key) is not null;

    public static void Upsert(Transaction tr, Database db, TerrainBoundarySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Key) || snapshot.Points.Count < 2)
        {
            return;
        }

        var list = LoadAll(tr, db).ToList();
        var idx = list.FindIndex(s =>
            string.Equals(s.Key, snapshot.Key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            list[idx] = snapshot;
        }
        else
        {
            list.Add(snapshot);
        }

        WriteAll(tr, db, list);
    }

    public static void Remove(Transaction tr, Database db, string key)
    {
        var list = LoadAll(tr, db)
            .Where(s => !string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        WriteAll(tr, db, list);
    }

    public static bool TryCaptureEntity(Entity entity, out List<Point3d> points, out bool closed)
    {
        points = [];
        closed = true;
        try
        {
            switch (entity)
            {
                case Polyline pl:
                    closed = pl.Closed;
                    for (var i = 0; i < pl.NumberOfVertices; i++)
                    {
                        var p2 = pl.GetPoint2dAt(i);
                        points.Add(new Point3d(p2.X, p2.Y, pl.Elevation));
                    }

                    return points.Count >= 2;

                case Polyline3d p3:
                    closed = p3.Closed;
                    foreach (ObjectId vid in p3)
                    {
                        DBObject? obj = null;
                        try
                        {
                            obj = vid.GetObject(OpenMode.ForRead, openErased: true);
                        }
                        catch
                        {
                            // ignore
                        }

                        if (obj is PolylineVertex3d v)
                        {
                            points.Add(v.Position);
                        }
                    }

                    return points.Count >= 2;

                case Curve curve:
                    closed = curve.Closed;
                    var start = curve.StartParam;
                    var end = curve.EndParam;
                    const int samples = 64;
                    for (var i = 0; i <= samples; i++)
                    {
                        var t = start + (end - start) * i / samples;
                        points.Add(curve.GetPointAtParameter(t));
                    }

                    return points.Count >= 2;

                default:
                    return false;
            }
        }
        catch
        {
            points = [];
            return false;
        }
    }

    private static void WriteAll(Transaction tr, Database db, List<TerrainBoundarySnapshot> list)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        if (dictionary is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(list, JsonOptions);
        var buffer = new ResultBuffer(new TypedValue((int)DxfCode.Text, json));
        if (dictionary.Contains(SnapshotsKey))
        {
            var existing = (Xrecord)tr.GetObject(dictionary.GetAt(SnapshotsKey), OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        var record = new Xrecord { Data = buffer };
        dictionary.SetAt(SnapshotsKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static DBDictionary? GetDictionary(Transaction tr, Database db, OpenMode mode)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            if (mode != OpenMode.ForWrite)
            {
                return null;
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
