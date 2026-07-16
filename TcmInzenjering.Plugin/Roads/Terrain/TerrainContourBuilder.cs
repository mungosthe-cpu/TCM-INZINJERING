using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal sealed class ContourPath
{
    public double Elevation { get; }
    public bool IsMajor { get; }
    public bool IsUser { get; }
    public IReadOnlyList<Point2d> Points { get; }

    public ContourPath(
        double elevation,
        bool isMajor,
        IReadOnlyList<Point2d> points,
        bool isUser = false)
    {
        Elevation = elevation;
        IsMajor = isMajor;
        IsUser = isUser;
        Points = points;
    }
}

/// <summary>
/// Civil-style konture (major/minor) iz TIN trouglova — meandering triangles + spajanje segmenata.
/// </summary>
internal static class TerrainContourBuilder
{
    private const double PointTol = 1e-5;
    private const double ElevTol = 1e-8;

    public static IReadOnlyList<ContourPath> Build(
        IReadOnlyList<TerrainTriangle> triangles,
        double minorInterval,
        double majorInterval,
        double baseElevation,
        IReadOnlyList<double>? userElevations = null)
    {
        if (triangles.Count == 0 || minorInterval <= 0)
        {
            return Array.Empty<ContourPath>();
        }

        var major = majorInterval > 0 ? majorInterval : minorInterval * 5;
        // Civil: major treba da deli se sa minor — prilagodi naviše ako treba.
        if (Math.Abs(major / minorInterval - Math.Round(major / minorInterval)) > 1e-9)
        {
            major = Math.Ceiling(major / minorInterval) * minorInterval;
        }

        var minZ = double.PositiveInfinity;
        var maxZ = double.NegativeInfinity;
        foreach (var t in triangles)
        {
            minZ = Math.Min(minZ, Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z)));
            maxZ = Math.Max(maxZ, Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z)));
        }

        if (double.IsInfinity(minZ) || maxZ - minZ < ElevTol)
        {
            return Array.Empty<ContourPath>();
        }

        var startN = (int)Math.Ceiling((minZ - baseElevation) / minorInterval - ElevTol);
        var endN = (int)Math.Floor((maxZ - baseElevation) / minorInterval + ElevTol);
        if (endN < startN && (userElevations is null || userElevations.Count == 0))
        {
            return Array.Empty<ContourPath>();
        }

        var result = new List<ContourPath>();
        var emitted = new HashSet<long>();
        if (endN >= startN)
        {
            for (var n = startN; n <= endN; n++)
            {
                var z = baseElevation + n * minorInterval;
                if (z < minZ - ElevTol || z > maxZ + ElevTol)
                {
                    continue;
                }

                AppendLevel(triangles, z, IsMajorLevel(z, baseElevation, major), isUser: false, result, emitted);
            }
        }

        if (userElevations is not null)
        {
            foreach (var z in userElevations)
            {
                if (z < minZ - ElevTol || z > maxZ + ElevTol)
                {
                    continue;
                }

                AppendLevel(triangles, z, isMajor: false, isUser: true, result, emitted);
            }
        }

        return result;
    }

    private static void AppendLevel(
        IReadOnlyList<TerrainTriangle> triangles,
        double z,
        bool isMajor,
        bool isUser,
        List<ContourPath> result,
        HashSet<long> emitted)
    {
        var key = (long)Math.Round(z / ElevTol);
        if (!emitted.Add(key))
        {
            return;
        }

        var segments = ExtractSegments(triangles, z);
        if (segments.Count == 0)
        {
            return;
        }

        foreach (var path in JoinSegments(segments))
        {
            if (path.Count >= 2)
            {
                result.Add(new ContourPath(z, isMajor, path, isUser));
            }
        }
    }

    private static bool IsMajorLevel(double z, double baseElevation, double majorInterval)
    {
        var rem = Math.Abs(Math.IEEERemainder(z - baseElevation, majorInterval));
        return rem <= ElevTol || Math.Abs(rem - majorInterval) <= ElevTol;
    }

    private static List<(Point2d A, Point2d B)> ExtractSegments(
        IReadOnlyList<TerrainTriangle> triangles,
        double z)
    {
        var segments = new List<(Point2d, Point2d)>();
        foreach (var t in triangles)
        {
            if (!TryContourSegment(t, z, out var a, out var b))
            {
                continue;
            }

            if (a.GetDistanceTo(b) < PointTol)
            {
                continue;
            }

            segments.Add((a, b));
        }

        return segments;
    }

    private static bool TryContourSegment(
        TerrainTriangle t,
        double z,
        out Point2d a,
        out Point2d b)
    {
        a = default;
        b = default;
        Span<Point2d> hits = stackalloc Point2d[3];
        var count = 0;

        TryEdge(t.A, t.B, z, hits, ref count);
        TryEdge(t.B, t.C, z, hits, ref count);
        TryEdge(t.C, t.A, z, hits, ref count);

        if (count < 2)
        {
            return false;
        }

        // Dva preseka → segment; tri (tema na konturi) → uzmi prva dva različita.
        a = hits[0];
        b = hits[1];
        if (a.GetDistanceTo(b) < PointTol && count >= 3)
        {
            b = hits[2];
        }

        return a.GetDistanceTo(b) >= PointTol;
    }

    private static void TryEdge(Point3d p, Point3d q, double z, Span<Point2d> hits, ref int count)
    {
        if (count >= 3)
        {
            return;
        }

        var dz = q.Z - p.Z;
        if (Math.Abs(dz) < ElevTol)
        {
            // Ravna ivica na nivou konture — ne uzimaj (izbegni duplikate); samo tačka ako baš na Z.
            return;
        }

        var t = (z - p.Z) / dz;
        if (t < -ElevTol || t > 1.0 + ElevTol)
        {
            return;
        }

        t = Math.Max(0, Math.Min(1, t));
        var x = p.X + t * (q.X - p.X);
        var y = p.Y + t * (q.Y - p.Y);
        var pt = new Point2d(x, y);

        for (var i = 0; i < count; i++)
        {
            if (hits[i].GetDistanceTo(pt) < PointTol)
            {
                return;
            }
        }

        hits[count++] = pt;
    }

    private static List<List<Point2d>> JoinSegments(List<(Point2d A, Point2d B)> segments)
    {
        var adj = new Dictionary<(long X, long Y), List<int>>();
        for (var i = 0; i < segments.Count; i++)
        {
            AddAdj(adj, Key(segments[i].A), i);
            AddAdj(adj, Key(segments[i].B), i);
        }

        var used = new bool[segments.Count];
        var paths = new List<List<Point2d>>();

        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            used[i] = true;
            var chain = new LinkedList<Point2d>();
            chain.AddLast(segments[i].A);
            chain.AddLast(segments[i].B);

            Extend(chain, adj, segments, used, forward: true);
            Extend(chain, adj, segments, used, forward: false);

            paths.Add(chain.ToList());
        }

        return paths;
    }

    private static void Extend(
        LinkedList<Point2d> chain,
        Dictionary<(long X, long Y), List<int>> adj,
        List<(Point2d A, Point2d B)> segments,
        bool[] used,
        bool forward)
    {
        while (true)
        {
            var tip = forward ? chain.Last!.Value : chain.First!.Value;
            var tipKey = Key(tip);
            if (!adj.TryGetValue(tipKey, out var list))
            {
                break;
            }

            var nextIdx = -1;
            foreach (var idx in list)
            {
                if (!used[idx])
                {
                    nextIdx = idx;
                    break;
                }
            }

            if (nextIdx < 0)
            {
                break;
            }

            used[nextIdx] = true;
            var (a, b) = segments[nextIdx];
            Point2d next;
            if (Key(a) == tipKey || a.GetDistanceTo(tip) < PointTol)
            {
                next = b;
            }
            else
            {
                next = a;
            }

            if (forward)
            {
                chain.AddLast(next);
            }
            else
            {
                chain.AddFirst(next);
            }
        }
    }

    private static void AddAdj(
        Dictionary<(long X, long Y), List<int>> adj,
        (long X, long Y) key,
        int segmentIndex)
    {
        if (!adj.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            adj[key] = list;
        }

        list.Add(segmentIndex);
    }

    private static (long X, long Y) Key(Point2d p) =>
        ((long)Math.Round(p.X / PointTol), (long)Math.Round(p.Y / PointTol));
}

/// <summary>Civil Contour Smoothing: Add Vertices (Chaikin) ili priprema tačaka za Spline.</summary>
internal static class ContourSmoother
{
    public static IReadOnlyList<Point2d> Apply(
        IReadOnlyList<Point2d> points,
        bool enabled,
        ContourSmoothType type,
        int factor)
    {
        if (!enabled || points.Count < 3 || factor <= 0)
        {
            return points;
        }

        // Civil slider 0–100 → broj Chaikin iteracija / gustina.
        var iterations = MathNet48.Clamp(1 + factor / 25, 1, 5);
        return type == ContourSmoothType.SplineCurve
            ? Densify(points, Math.Max(1, factor / 20))
            : Chaikin(points, iterations);
    }

    private static List<Point2d> Densify(IReadOnlyList<Point2d> points, int subdiv)
    {
        if (subdiv <= 1 || points.Count < 2)
        {
            return points.ToList();
        }

        var result = new List<Point2d>(points.Count * subdiv);
        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            result.Add(a);
            for (var s = 1; s < subdiv; s++)
            {
                var t = s / (double)subdiv;
                result.Add(new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }

        result.Add(points[^1]);
        return result;
    }

    private static List<Point2d> Chaikin(IReadOnlyList<Point2d> points, int iterations)
    {
        var current = points.ToList();
        for (var iter = 0; iter < iterations; iter++)
        {
            if (current.Count < 3)
            {
                break;
            }

            var next = new List<Point2d>(current.Count * 2);
            next.Add(current[0]);
            for (var i = 0; i < current.Count - 1; i++)
            {
                var p = current[i];
                var q = current[i + 1];
                next.Add(new Point2d(0.75 * p.X + 0.25 * q.X, 0.75 * p.Y + 0.25 * q.Y));
                next.Add(new Point2d(0.25 * p.X + 0.75 * q.X, 0.25 * p.Y + 0.75 * q.Y));
            }

            next.Add(current[^1]);
            current = next;
        }

        return current;
    }
}
