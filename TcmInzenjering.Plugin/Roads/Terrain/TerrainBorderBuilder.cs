using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Civil Surface Border: spoljna granica TIN-a (ivice koje pripadaju samo jednom trouglu).
/// </summary>
internal static class TerrainBorderBuilder
{
    private const double Tol = 1e-6;

    /// <summary>Vraća zatvoreni prsten tačaka (bez duplog poslednjeg = prvog).</summary>
    public static IReadOnlyList<Point3d> BuildOuterRing(
        IReadOnlyList<Point3d> vertices,
        IReadOnlyList<TerrainDelaunay.Triangle> triangles)
    {
        if (vertices.Count < 3 || triangles.Count == 0)
        {
            return Array.Empty<Point3d>();
        }

        var edgeUse = new Dictionary<(int A, int B), int>();
        void AddEdge(int i, int j)
        {
            var key = i < j ? (i, j) : (j, i);
            edgeUse[key] = edgeUse.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var t in triangles)
        {
            AddEdge(t.A, t.B);
            AddEdge(t.B, t.C);
            AddEdge(t.C, t.A);
        }

        var adj = new Dictionary<int, List<int>>();
        void Link(int a, int b)
        {
            if (!adj.TryGetValue(a, out var list))
            {
                list = new List<int>(2);
                adj[a] = list;
            }

            if (!list.Contains(b))
            {
                list.Add(b);
            }
        }

        foreach (var kv in edgeUse)
        {
            var a = kv.Key.A;
            var b = kv.Key.B;
            var count = kv.Value;
            if (count != 1)
            {
                continue;
            }

            Link(a, b);
            Link(b, a);
        }

        if (adj.Count < 3)
        {
            return Array.Empty<Point3d>();
        }

        // Najduži zatvoreni boundary loop (exterior).
        var best = new List<int>();
        var visitedEdge = new HashSet<(int, int)>();

        foreach (var start in adj.Keys.OrderBy(i => i))
        {
            foreach (var firstNext in adj[start])
            {
                var e0 = NormEdge(start, firstNext);
                if (!visitedEdge.Add(e0))
                {
                    continue;
                }

                var loop = Walk(start, firstNext, adj, visitedEdge);
                if (loop.Count > best.Count)
                {
                    best = loop;
                }
            }
        }

        if (best.Count < 3)
        {
            return Array.Empty<Point3d>();
        }

        return best.Select(i => vertices[i]).ToList();
    }

    private static List<int> Walk(
        int start,
        int next,
        Dictionary<int, List<int>> adj,
        HashSet<(int, int)> visitedEdge)
    {
        var loop = new List<int> { start };
        var prev = start;
        var curr = next;
        var guard = 0;
        while (curr != start && guard++ < 100000)
        {
            loop.Add(curr);
            visitedEdge.Add(NormEdge(prev, curr));
            if (!adj.TryGetValue(curr, out var neighbors) || neighbors.Count == 0)
            {
                break;
            }

            var onward = -1;
            foreach (var n in neighbors)
            {
                if (n == prev)
                {
                    continue;
                }

                onward = n;
                // Prefer unvisited boundary edge.
                if (!visitedEdge.Contains(NormEdge(curr, n)))
                {
                    break;
                }
            }

            if (onward < 0)
            {
                // only back — closed?
                if (neighbors.Contains(start))
                {
                    onward = start;
                }
                else
                {
                    break;
                }
            }

            prev = curr;
            curr = onward;
        }

        return loop;
    }

    private static (int, int) NormEdge(int a, int b) => a < b ? (a, b) : (b, a);

    public static bool NearlyEqualXy(Point3d a, Point3d b) =>
        Math.Abs(a.X - b.X) <= Tol && Math.Abs(a.Y - b.Y) <= Tol;
}
