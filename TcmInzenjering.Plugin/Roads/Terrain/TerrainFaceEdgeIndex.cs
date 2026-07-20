using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Snapshot indeks 3DFACE ivica za brzo pojedinačno brisanje bez ponovnog
/// skeniranja celog ModelSpace-a na svaki klik.
/// </summary>
internal sealed class TerrainFaceEdgeIndex
{
    private readonly List<(ObjectId Id, Point3d[] Verts)> _faces = [];
    private readonly Dictionary<(long, long), List<int>> _edgeToFaces = new();

    public int FaceCount => _faces.Count(f => !f.Id.IsNull);

    public static TerrainFaceEdgeIndex Build(Transaction tr, Database db)
    {
        var index = new TerrainFaceEdgeIndex();
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Face face || face.IsErased)
            {
                continue;
            }

            if (!TerrainFaceXData.IsTerrainFace(face) &&
                !TerrainLayerNames.IsBaseOrPrefixed(face.Layer, RoadCommands.TerrainLayerName))
            {
                continue;
            }

            var verts = GetTriangleVertices(face);
            if (verts.Length < 3)
            {
                continue;
            }

            var faceIndex = index._faces.Count;
            index._faces.Add((id, verts));
            index.IndexEdge(faceIndex, verts[0], verts[1]);
            index.IndexEdge(faceIndex, verts[1], verts[2]);
            index.IndexEdge(faceIndex, verts[2], verts[0]);
        }

        return index;
    }

    public bool TryFindEdgeNear(
        Point3d pick,
        double tolerance,
        out Point3d edgeA,
        out Point3d edgeB,
        out IReadOnlyList<ObjectId> faceIds)
    {
        edgeA = default;
        edgeB = default;
        faceIds = Array.Empty<ObjectId>();
        var bestDist = tolerance;
        var bestKey = default((long, long));
        var found = false;

        for (var i = 0; i < _faces.Count; i++)
        {
            var (id, verts) = _faces[i];
            if (id.IsNull || verts.Length < 3)
            {
                continue;
            }

            for (var e = 0; e < 3; e++)
            {
                var a = verts[e];
                var b = verts[(e + 1) % 3];
                var dist = DistancePointToSegmentXy(pick, a, b);
                if (dist > bestDist)
                {
                    continue;
                }

                bestDist = dist;
                edgeA = a;
                edgeB = b;
                bestKey = NormalizeKey(a, b);
                found = true;
            }
        }

        if (!found || !_edgeToFaces.TryGetValue(bestKey, out var faceIndexes))
        {
            return found;
        }

        faceIds = faceIndexes
            .Where(i => i >= 0 && i < _faces.Count && !_faces[i].Id.IsNull)
            .Select(i => _faces[i].Id)
            .Distinct()
            .ToList();
        return faceIds.Count > 0;
    }

    public void RemoveFaces(IEnumerable<ObjectId> faceIds)
    {
        var remove = new HashSet<ObjectId>(faceIds);
        for (var i = 0; i < _faces.Count; i++)
        {
            if (remove.Contains(_faces[i].Id))
            {
                _faces[i] = (ObjectId.Null, _faces[i].Verts);
            }
        }
    }

    private void IndexEdge(int faceIndex, Point3d a, Point3d b)
    {
        var key = NormalizeKey(a, b);
        if (!_edgeToFaces.TryGetValue(key, out var list))
        {
            list = [];
            _edgeToFaces[key] = list;
        }

        if (!list.Contains(faceIndex))
        {
            list.Add(faceIndex);
        }
    }

    private static (long, long) NormalizeKey(Point3d a, Point3d b)
    {
        // 1e-4 m kvantizacija — dovoljna za pick/toleranciju 1.0
        var ax = (long)Math.Round(a.X * 10000.0);
        var ay = (long)Math.Round(a.Y * 10000.0);
        var bx = (long)Math.Round(b.X * 10000.0);
        var by = (long)Math.Round(b.Y * 10000.0);
        var ha = HashXy(ax, ay);
        var hb = HashXy(bx, by);
        return ha <= hb ? (ha, hb) : (hb, ha);
    }

    private static long HashXy(long x, long y) =>
        (x * 73856093L) ^ (y * 19349663L);

    private static Point3d[] GetTriangleVertices(Face face)
    {
        try
        {
            return
            [
                face.GetVertexAt(0),
                face.GetVertexAt(1),
                face.GetVertexAt(2)
            ];
        }
        catch
        {
            return [];
        }
    }

    private static double DistancePointToSegmentXy(Point3d p, Point3d a, Point3d b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-18)
        {
            var ex = p.X - a.X;
            var ey = p.Y - a.Y;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = Math.Max(0, Math.Min(1, t));
        var qx = a.X + t * dx - p.X;
        var qy = a.Y + t * dy - p.Y;
        return Math.Sqrt(qx * qx + qy * qy);
    }
}
