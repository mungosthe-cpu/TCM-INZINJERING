using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Kombinovani teren: 3DFACE/Mesh trouglovi + Civil TinSurface (FindElevationAtXY).
/// </summary>
internal sealed class TerrainElevationModel
{
    private readonly TerrainMesh _mesh;
    private readonly IReadOnlyList<Entity> _tinSurfaces;

    public int TriangleCount => _mesh.TriangleCount;
    public int TinSurfaceCount => _tinSurfaces.Count;
    public bool HasTerrain => TriangleCount > 0 || TinSurfaceCount > 0;

    public TerrainElevationModel(TerrainMesh mesh, IReadOnlyList<Entity> tinSurfaces)
    {
        _mesh = mesh;
        _tinSurfaces = tinSurfaces;
    }

    public bool TryGetElevation(double x, double y, out double z)
    {
        if (_mesh.TriangleCount > 0 && _mesh.TryGetElevation(x, y, out z))
        {
            return true;
        }

        foreach (var surface in _tinSurfaces)
        {
            if (TinSurfaceInterop.TryFindElevation(surface, x, y, out z))
            {
                return true;
            }
        }

        z = 0;
        return false;
    }

    public IEnumerable<(Point2d P, Point2d Q)> EnumeratePlanEdgesNear(Point2d a, Point2d b) =>
        _mesh.EnumeratePlanEdgesNear(a, b);

    /// <summary>
    /// Preferiraj Civil SampleElevations između A–B ako ima TinSurface.
    /// </summary>
    public IReadOnlyList<Point3d> SampleAlongPlanSegment(Point2d a, Point2d b)
    {
        if (_tinSurfaces.Count == 0)
        {
            return Array.Empty<Point3d>();
        }

        var start = new Point3d(a.X, a.Y, 0);
        var end = new Point3d(b.X, b.Y, 0);
        foreach (var surface in _tinSurfaces)
        {
            var samples = TinSurfaceInterop.SampleElevationsAlong(surface, start, end);
            if (samples.Count >= 2)
            {
                return samples;
            }
        }

        return Array.Empty<Point3d>();
    }
}
