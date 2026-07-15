using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Civil 3D TinSurface bez compile-time reference na AeccDbMgd (reflection).
/// Radi kad je Civil ili Object Enabler učitan.
/// </summary>
internal static class TinSurfaceInterop
{
    public static bool IsTinSurface(Entity entity)
    {
        if (entity is null)
        {
            return false;
        }

        var typeName = entity.GetType().Name;
        if (ContainsIgnoreCase(typeName, "TinSurface") ||
            ContainsIgnoreCase(typeName, "GridSurface"))
        {
            return true;
        }

        try
        {
            var rx = entity.GetRXClass()?.Name ?? string.Empty;
            return ContainsIgnoreCase(rx, "TinSurface") ||
                   ContainsIgnoreCase(rx, "GridSurface") ||
                   ContainsIgnoreCase(rx, "AeccDbSurface");
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFindElevation(Entity surface, double x, double y, out double z)
    {
        z = 0;
        if (surface is null)
        {
            return false;
        }

        try
        {
            var method = surface.GetType().GetMethod(
                "FindElevationAtXY",
                [typeof(double), typeof(double)]);
            if (method is null)
            {
                return false;
            }

            var result = method.Invoke(surface, [x, y]);
            if (result is null)
            {
                return false;
            }

            z = Convert.ToDouble(result);
            return !double.IsNaN(z) && !double.IsInfinity(z);
        }
        catch
        {
            // PointNotOnEntityException ili surface van opsega
            return false;
        }
    }

    /// <summary>
    /// Pokušaj da izvuče TIN trouglove (za preseke sa ivicama). Može biti sporo na velikim surface-ima.
    /// </summary>
    public static int TryAppendTriangles(Entity surface, List<TerrainTriangle> triangles)
    {
        if (surface is null)
        {
            return 0;
        }

        var before = triangles.Count;
        try
        {
            // Tipično: surface.Triangles → kolekcija sa Vertex1/2/3.Location
            var trianglesProp = surface.GetType().GetProperty("Triangles");
            var collection = trianglesProp?.GetValue(surface);
            if (collection is System.Collections.IEnumerable enumerable)
            {
                foreach (var tri in enumerable)
                {
                    if (tri is null)
                    {
                        continue;
                    }

                    if (!TryReadTriangleVertices(tri, out var a, out var b, out var c))
                    {
                        continue;
                    }

                    triangles.Add(new TerrainTriangle(a, b, c));
                }
            }
        }
        catch
        {
            // Ignoriši — elevacija i dalje ide preko FindElevationAtXY.
        }

        return triangles.Count - before;
    }

    private static bool TryReadTriangleVertices(object tri, out Point3d a, out Point3d b, out Point3d c)
    {
        a = b = c = Point3d.Origin;
        var type = tri.GetType();
        if (!TryVertexLocation(type, tri, "Vertex1", out a) ||
            !TryVertexLocation(type, tri, "Vertex2", out b) ||
            !TryVertexLocation(type, tri, "Vertex3", out c))
        {
            return false;
        }

        return true;
    }

    private static bool TryVertexLocation(Type triType, object tri, string propertyName, out Point3d point)
    {
        point = Point3d.Origin;
        var vertexProp = triType.GetProperty(propertyName);
        var vertex = vertexProp?.GetValue(tri);
        if (vertex is null)
        {
            return false;
        }

        var locProp = vertex.GetType().GetProperty("Location");
        if (locProp?.GetValue(vertex) is Point3d p)
        {
            point = p;
            return true;
        }

        return false;
    }

    /// <summary>
    /// SampleElevations(start, end) — Civil vraća tačke duž segmenta sa Z na surface-u.
    /// </summary>
    public static IReadOnlyList<Point3d> SampleElevationsAlong(Entity surface, Point3d start, Point3d end)
    {
        if (surface is null)
        {
            return Array.Empty<Point3d>();
        }

        try
        {
            var method = surface.GetType().GetMethod(
                "SampleElevations",
                [typeof(Point3d), typeof(Point3d)]);
            if (method is null)
            {
                return Array.Empty<Point3d>();
            }

            var result = method.Invoke(surface, [start, end]);
            return result switch
            {
                Point3dCollection coll => coll.Cast<Point3d>().ToList(),
                IEnumerable<Point3d> seq => seq.ToList(),
                _ => Array.Empty<Point3d>()
            };
        }
        catch
        {
            return Array.Empty<Point3d>();
        }
    }

    private static bool ContainsIgnoreCase(string value, string fragment) =>
        value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
}
