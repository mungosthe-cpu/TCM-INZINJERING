using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// 2D Delaunay (Bowyer–Watson) u XY — kao Civil 3D TIN od tačaka.
/// Z se čuva na temenima; trougao se koristi za 3DFACE.
/// </summary>
internal static class TerrainDelaunay
{
    public readonly record struct Triangle(int A, int B, int C);

    public static IReadOnlyList<Triangle> Triangulate(IReadOnlyList<Point3d> points)
    {
        if (points.Count < 3)
        {
            return Array.Empty<Triangle>();
        }

        // Deduplicate near-identical XY (keep first Z).
        var unique = new List<Point3d>(points.Count);
        const double tol = 1e-8;
        foreach (var p in points)
        {
            var isDup = false;
            for (var i = 0; i < unique.Count; i++)
            {
                var q = unique[i];
                if (Math.Abs(p.X - q.X) <= tol && Math.Abs(p.Y - q.Y) <= tol)
                {
                    isDup = true;
                    break;
                }
            }

            if (!isDup)
            {
                unique.Add(p);
            }
        }

        if (unique.Count < 3)
        {
            return Array.Empty<Triangle>();
        }

        var minX = unique[0].X;
        var minY = unique[0].Y;
        var maxX = minX;
        var maxY = minY;
        foreach (var p in unique)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        var dx = maxX - minX;
        var dy = maxY - minY;
        var dmax = Math.Max(dx, dy);
        if (dmax < 1e-9)
        {
            return Array.Empty<Triangle>();
        }

        var midX = (minX + maxX) * 0.5;
        var midY = (minY + maxY) * 0.5;

        // Super-triangle vertices appended after real points.
        var n = unique.Count;
        var verts = new List<Point3d>(n + 3);
        verts.AddRange(unique);
        verts.Add(new Point3d(midX - 2 * dmax, midY - dmax, 0));
        verts.Add(new Point3d(midX, midY + 2 * dmax, 0));
        verts.Add(new Point3d(midX + 2 * dmax, midY - dmax, 0));

        var tris = new List<Triangle> { new(n, n + 1, n + 2) };

        for (var i = 0; i < n; i++)
        {
            var p = verts[i];
            var bad = new List<int>();
            for (var t = 0; t < tris.Count; t++)
            {
                var tri = tris[t];
                if (InCircumcircle(p, verts[tri.A], verts[tri.B], verts[tri.C]))
                {
                    bad.Add(t);
                }
            }

            var edgeCount = new Dictionary<(int, int), int>();
            foreach (var t in bad)
            {
                AddEdge(edgeCount, tris[t].A, tris[t].B);
                AddEdge(edgeCount, tris[t].B, tris[t].C);
                AddEdge(edgeCount, tris[t].C, tris[t].A);
            }

            // Remove bad triangles (from end).
            bad.Sort();
            for (var b = bad.Count - 1; b >= 0; b--)
            {
                tris.RemoveAt(bad[b]);
            }

            foreach (var kv in edgeCount)
            {
                if (kv.Value != 1)
                {
                    continue;
                }

                tris.Add(new Triangle(kv.Key.Item1, kv.Key.Item2, i));
            }
        }

        // Drop any triangle that touches the super-triangle.
        var result = new List<Triangle>();
        foreach (var tri in tris)
        {
            if (tri.A >= n || tri.B >= n || tri.C >= n)
            {
                continue;
            }

            // Skip degenerate (near-collinear) triangles.
            if (Area2(verts[tri.A], verts[tri.B], verts[tri.C]) < 1e-12)
            {
                continue;
            }

            result.Add(tri);
        }

        return result;
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        edges.TryGetValue(key, out var count);
        edges[key] = count + 1;
    }

    private static double Area2(Point3d a, Point3d b, Point3d c) =>
        Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));

    private static bool InCircumcircle(Point3d p, Point3d a, Point3d b, Point3d c)
    {
        var ax = a.X - p.X;
        var ay = a.Y - p.Y;
        var bx = b.X - p.X;
        var by = b.Y - p.Y;
        var cx = c.X - p.X;
        var cy = c.Y - p.Y;

        var det = (ax * ax + ay * ay) * (bx * cy - cx * by)
                  - (bx * bx + by * by) * (ax * cy - cx * ay)
                  + (cx * cx + cy * cy) * (ax * by - bx * ay);

        // Orientation of ABC: positive = CCW.
        var orient = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return orient > 0 ? det > 0 : det < 0;
    }
}
