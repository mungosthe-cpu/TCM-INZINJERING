using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Trougaona mreža terena sa XY grid indeksom za brze elevacijske upite.
/// </summary>
internal sealed class TerrainMesh
{
    private readonly IReadOnlyList<TerrainTriangle> _triangles;
    private readonly Dictionary<(int, int), List<int>> _grid;
    private readonly double _cellSize;
    private readonly double _originX;
    private readonly double _originY;

    public int TriangleCount => _triangles.Count;

    public TerrainMesh(IReadOnlyList<TerrainTriangle> triangles, double cellSize = 25.0)
    {
        _triangles = triangles;
        if (triangles.Count == 0)
        {
            _grid = new Dictionary<(int, int), List<int>>();
            _cellSize = cellSize;
            _originX = 0;
            _originY = 0;
            return;
        }

        _originX = triangles.Min(t => t.MinX);
        _originY = triangles.Min(t => t.MinY);
        var spanX = triangles.Max(t => t.MaxX) - _originX;
        var spanY = triangles.Max(t => t.MaxY) - _originY;
        var autoCell = Math.Max(5.0, Math.Sqrt((spanX * spanY) / Math.Max(1, triangles.Count)));
        _cellSize = Math.Clamp(cellSize > 0 ? cellSize : autoCell, 5.0, 200.0);

        _grid = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < triangles.Count; i++)
        {
            var t = triangles[i];
            var x0 = CellX(t.MinX);
            var x1 = CellX(t.MaxX);
            var y0 = CellY(t.MinY);
            var y1 = CellY(t.MaxY);
            for (var cx = x0; cx <= x1; cx++)
            {
                for (var cy = y0; cy <= y1; cy++)
                {
                    var key = (cx, cy);
                    if (!_grid.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        _grid[key] = list;
                    }

                    list.Add(i);
                }
            }
        }
    }

    public bool TryGetElevation(double x, double y, out double z)
    {
        z = 0;
        if (_triangles.Count == 0)
        {
            return false;
        }

        if (!_grid.TryGetValue((CellX(x), CellY(y)), out var candidates))
        {
            // Fallback: scan neighbors / all if na ivici celije
            candidates = null;
        }

        if (candidates is not null)
        {
            foreach (var index in candidates)
            {
                if (_triangles[index].TryGetElevation(x, y, out z))
                {
                    return true;
                }
            }
        }

        // Sporiji fallback — tačka blizu ivice grid ćelije
        for (var i = 0; i < _triangles.Count; i++)
        {
            if (_triangles[i].TryGetElevation(x, y, out z))
            {
                return true;
            }
        }

        return false;
    }

    public IEnumerable<(Point2d P, Point2d Q)> EnumeratePlanEdgesNear(Point2d a, Point2d b)
    {
        var minX = Math.Min(a.X, b.X);
        var maxX = Math.Max(a.X, b.X);
        var minY = Math.Min(a.Y, b.Y);
        var maxY = Math.Max(a.Y, b.Y);
        var x0 = CellX(minX);
        var x1 = CellX(maxX);
        var y0 = CellY(minY);
        var y1 = CellY(maxY);

        var seen = new HashSet<int>();
        for (var cx = x0; cx <= x1; cx++)
        {
            for (var cy = y0; cy <= y1; cy++)
            {
                if (!_grid.TryGetValue((cx, cy), out var list))
                {
                    continue;
                }

                foreach (var index in list)
                {
                    if (!seen.Add(index))
                    {
                        continue;
                    }

                    foreach (var edge in _triangles[index].GetPlanEdges())
                    {
                        yield return edge;
                    }
                }
            }
        }
    }

    private int CellX(double x) => (int)Math.Floor((x - _originX) / _cellSize);
    private int CellY(double y) => (int)Math.Floor((y - _originY) / _cellSize);
}

internal static class TerrainMeshBuilder
{
    public static TerrainElevationModel Build(Transaction tr, IEnumerable<ObjectId> entityIds)
    {
        var triangles = new List<TerrainTriangle>();
        var tinSurfaces = new List<Entity>();

        foreach (var id in entityIds)
        {
            if (id.IsNull || id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (TinSurfaceInterop.IsTinSurface(entity))
            {
                tinSurfaces.Add(entity);
                // Izvuci TIN ivice radi režima "Preseci" (bez automatskog SampleElevations na 2 m).
                TinSurfaceInterop.TryAppendTriangles(entity, triangles);
                continue;
            }

            AppendTriangles(entity, triangles);
        }

        return new TerrainElevationModel(new TerrainMesh(triangles), tinSurfaces);
    }

    private static void AppendTriangles(Entity entity, List<TerrainTriangle> triangles)
    {
        switch (entity)
        {
            case Face face:
                AppendFace(face, triangles);
                break;
            case PolyFaceMesh pfm:
                AppendPolyFaceMesh(pfm, triangles);
                break;
            case SubDMesh mesh:
                AppendSubDMesh(mesh, triangles);
                break;
        }
    }

    private static void AppendFace(Face face, List<TerrainTriangle> triangles)
    {
        var p0 = face.GetVertexAt(0);
        var p1 = face.GetVertexAt(1);
        var p2 = face.GetVertexAt(2);
        var p3 = face.GetVertexAt(3);

        AddTriangle(triangles, p0, p1, p2);
        if (p3.DistanceTo(p2) > 1e-9 && p3.DistanceTo(p0) > 1e-9)
        {
            AddTriangle(triangles, p0, p2, p3);
        }
    }

    private static void AppendPolyFaceMesh(PolyFaceMesh mesh, List<TerrainTriangle> triangles)
    {
        var vertices = new List<Point3d>();
        var faces = new List<(int A, int B, int C, int D)>();

        foreach (ObjectId id in mesh)
        {
            var obj = id.Database.TransactionManager.TopTransaction.GetObject(id, OpenMode.ForRead);
            switch (obj)
            {
                case PolyFaceMeshVertex vertex:
                    vertices.Add(vertex.Position);
                    break;
                case FaceRecord face:
                {
                    var i0 = Math.Abs((int)face.GetVertexAt(0));
                    var i1 = Math.Abs((int)face.GetVertexAt(1));
                    var i2 = Math.Abs((int)face.GetVertexAt(2));
                    var i3 = Math.Abs((int)face.GetVertexAt(3));
                    faces.Add((i0, i1, i2, i3));
                    break;
                }
            }
        }

        foreach (var (a, b, c, d) in faces)
        {
            if (a <= 0 || b <= 0 || c <= 0 ||
                a > vertices.Count || b > vertices.Count || c > vertices.Count)
            {
                continue;
            }

            AddTriangle(triangles, vertices[a - 1], vertices[b - 1], vertices[c - 1]);
            if (d > 0 && d <= vertices.Count && d != c)
            {
                AddTriangle(triangles, vertices[a - 1], vertices[c - 1], vertices[d - 1]);
            }
        }
    }

    private static void AppendSubDMesh(SubDMesh mesh, List<TerrainTriangle> triangles)
    {
        try
        {
            var verts = mesh.Vertices;
            var faceArr = mesh.FaceArray;
            if (verts is null || faceArr is null || faceArr.Count == 0)
            {
                return;
            }

            var i = 0;
            while (i < faceArr.Count)
            {
                var n = faceArr[i];
                if (n < 3 || i + n >= faceArr.Count)
                {
                    break;
                }

                var i0 = faceArr[i + 1];
                for (var k = 1; k < n - 1; k++)
                {
                    var i1 = faceArr[i + 1 + k];
                    var i2 = faceArr[i + 2 + k];
                    if (i0 < 0 || i1 < 0 || i2 < 0 ||
                        i0 >= verts.Count || i1 >= verts.Count || i2 >= verts.Count)
                    {
                        continue;
                    }

                    AddTriangle(triangles, verts[i0], verts[i1], verts[i2]);
                }

                i += n + 1;
            }
        }
        catch
        {
            // SubDMesh API može da varira — ignoriši ovaj entitet.
        }
    }

    private static void AddTriangle(List<TerrainTriangle> triangles, Point3d a, Point3d b, Point3d c)
    {
        var ab = b - a;
        var ac = c - a;
        var cross = ab.X * ac.Y - ab.Y * ac.X;
        if (Math.Abs(cross) < 1e-12 && Math.Abs(ab.Z * ac.Y - ab.Y * ac.Z) < 1e-12)
        {
            // Degenerisan u 3D / XY — preskoči skoro kolinearne plan-projekcije
            var area2 = Math.Abs(cross);
            if (area2 < 1e-12)
            {
                return;
            }
        }

        triangles.Add(new TerrainTriangle(a, b, c));
    }
}
