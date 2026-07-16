using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal sealed class TerrainWatershedResult
{
    public required IReadOnlyList<int> BasinIdByTriangle { get; init; }
    public required int BasinCount { get; init; }
    public required IReadOnlyList<IReadOnlyList<Point2d>> BasinOutlines { get; init; }
}

/// <summary>
/// Lite watershed na TIN grafu: svaki trougao teče ka najstrmijem nižem susedu;
/// sink / izlaz na granicu = ID sliva; spoljne ivice sliva → polilinije.
/// </summary>
internal static class TerrainWatershedBuilder
{
    private const double EdgeTol = 1e-4;

    public static TerrainWatershedResult Build(IReadOnlyList<TerrainTriangle> triangles)
    {
        var n = triangles.Count;
        if (n == 0)
        {
            return new TerrainWatershedResult
            {
                BasinIdByTriangle = Array.Empty<int>(),
                BasinCount = 0,
                BasinOutlines = Array.Empty<IReadOnlyList<Point2d>>()
            };
        }

        var samples = new TerrainSlopeSample[n];
        var centroids = new Point3d[n];
        for (var i = 0; i < n; i++)
        {
            samples[i] = TerrainSlopeMath.Analyze(triangles[i]);
            centroids[i] = samples[i].Centroid;
        }

        var neighbors = BuildNeighbors(triangles);
        var next = new int[n];
        for (var i = 0; i < n; i++)
        {
            next[i] = ChooseDownhillNeighbor(i, samples, neighbors, centroids);
        }

        // Flow path → sink index (self if local sink / leave-boundary).
        var sink = new int[n];
        for (var i = 0; i < n; i++)
        {
            sink[i] = TraceSink(i, next);
        }

        var sinkToBasin = new Dictionary<int, int>();
        var basinByTri = new int[n];
        var basinCount = 0;
        for (var i = 0; i < n; i++)
        {
            var s = sink[i];
            if (!sinkToBasin.TryGetValue(s, out var bid))
            {
                bid = basinCount++;
                sinkToBasin[s] = bid;
            }

            basinByTri[i] = bid;
        }

        var outlines = ExtractBasinOutlines(triangles, basinByTri, basinCount);
        return new TerrainWatershedResult
        {
            BasinIdByTriangle = basinByTri,
            BasinCount = basinCount,
            BasinOutlines = outlines
        };
    }

    private static List<int>[] BuildNeighbors(IReadOnlyList<TerrainTriangle> triangles)
    {
        var n = triangles.Count;
        var neighbors = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            neighbors[i] = [];
        }

        var edgeMap = new Dictionary<(long, long, long, long), int>(n * 3);
        for (var i = 0; i < n; i++)
        {
            foreach (var (p, q) in triangles[i].GetPlanEdges())
            {
                var key = EdgeKey(p, q);
                if (edgeMap.TryGetValue(key, out var other))
                {
                    if (other != i)
                    {
                        neighbors[i].Add(other);
                        neighbors[other].Add(i);
                    }
                }
                else
                {
                    edgeMap[key] = i;
                }
            }
        }

        return neighbors;
    }

    private static int ChooseDownhillNeighbor(
        int i,
        TerrainSlopeSample[] samples,
        List<int>[] neighbors,
        Point3d[] centroids)
    {
        var z0 = centroids[i].Z;
        var sample = samples[i];
        if (sample.IsFlat || neighbors[i].Count == 0)
        {
            return i; // sink
        }

        var best = -1;
        var bestScore = double.PositiveInfinity;

        foreach (var j in neighbors[i])
        {
            var zj = centroids[j].Z;
            if (zj >= z0 - 1e-6)
            {
                continue; // mora biti niže
            }

            var dx = centroids[j].X - centroids[i].X;
            var dy = centroids[j].Y - centroids[i].Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12)
            {
                continue;
            }

            // Prefer suseda u smeru flow + pad u Z.
            var align = sample.FlowDirection.X * (dx / len) + sample.FlowDirection.Y * (dy / len);
            var drop = z0 - zj;
            // Manji score = bolje (više drop, bolji align).
            var score = -drop * 10.0 - align;
            if (score < bestScore)
            {
                bestScore = score;
                best = j;
            }
        }

        return best < 0 ? i : best;
    }

    private static int TraceSink(int start, int[] next)
    {
        var slow = start;
        var fast = start;
        // Floyd cycle + max hops.
        for (var hop = 0; hop < next.Length + 2; hop++)
        {
            var n1 = next[slow];
            if (n1 == slow)
            {
                return slow;
            }

            slow = n1;
            fast = next[next[fast]];
            if (slow == fast)
            {
                // Ciklus — najniži čvor u ciklusu kao sink.
                return LowestInCycle(slow, next);
            }
        }

        return slow;
    }

    private static int LowestInCycle(int node, int[] next)
    {
        var start = node;
        var best = node;
        do
        {
            node = next[node];
            if (node < best)
            {
                best = node;
            }
        } while (node != start);

        return best;
    }

    private static List<IReadOnlyList<Point2d>> ExtractBasinOutlines(
        IReadOnlyList<TerrainTriangle> triangles,
        int[] basinByTri,
        int basinCount)
    {
        // Boundary segments per basin: edge shared with other basin or mesh exterior.
        var segmentsByBasin = new List<(Point2d A, Point2d B)>[basinCount];
        for (var b = 0; b < basinCount; b++)
        {
            segmentsByBasin[b] = [];
        }

        var edgeOwner = new Dictionary<(long, long, long, long), (int Tri, Point2d A, Point2d B)>();
        for (var i = 0; i < triangles.Count; i++)
        {
            foreach (var (p, q) in triangles[i].GetPlanEdges())
            {
                var key = EdgeKey(p, q);
                if (edgeOwner.TryGetValue(key, out var other))
                {
                    var b0 = basinByTri[i];
                    var b1 = basinByTri[other.Tri];
                    if (b0 != b1)
                    {
                        segmentsByBasin[b0].Add((p, q));
                        segmentsByBasin[b1].Add((other.A, other.B));
                    }

                    edgeOwner.Remove(key);
                }
                else
                {
                    edgeOwner[key] = (i, p, q);
                }
            }
        }

        // Exterior edges.
        foreach (var kv in edgeOwner)
        {
            var edge = kv.Value;
            segmentsByBasin[basinByTri[edge.Tri]].Add((edge.A, edge.B));
        }

        var outlines = new List<IReadOnlyList<Point2d>>();
        for (var b = 0; b < basinCount; b++)
        {
            foreach (var ring in ChainSegments(segmentsByBasin[b]))
            {
                if (ring.Count >= 3)
                {
                    outlines.Add(ring);
                }
            }
        }

        return outlines;
    }

    private static List<List<Point2d>> ChainSegments(List<(Point2d A, Point2d B)> segments)
    {
        var result = new List<List<Point2d>>();
        if (segments.Count == 0)
        {
            return result;
        }

        var unused = new HashSet<int>();
        for (var i = 0; i < segments.Count; i++)
        {
            unused.Add(i);
        }

        // Index endpoints → segment indices.
        var at = new Dictionary<(long, long), List<int>>();
        void AddEnd(Point2d p, int idx)
        {
            var k = PointKey(p);
            if (!at.TryGetValue(k, out var list))
            {
                list = [];
                at[k] = list;
            }

            list.Add(idx);
        }

        for (var i = 0; i < segments.Count; i++)
        {
            AddEnd(segments[i].A, i);
            AddEnd(segments[i].B, i);
        }

        while (unused.Count > 0)
        {
            var startIdx = unused.First();
            unused.Remove(startIdx);
            var seg = segments[startIdx];
            var chain = new List<Point2d> { seg.A, seg.B };
            var head = seg.A;
            var tail = seg.B;

            var growing = true;
            while (growing)
            {
                growing = false;
                if (TryExtend(ref tail, chain, append: true))
                {
                    growing = true;
                }

                if (TryExtend(ref head, chain, append: false))
                {
                    growing = true;
                }
            }

            result.Add(chain);

            bool TryExtend(ref Point2d end, List<Point2d> path, bool append)
            {
                var k = PointKey(end);
                if (!at.TryGetValue(k, out var cand))
                {
                    return false;
                }

                foreach (var idx in cand)
                {
                    if (!unused.Contains(idx))
                    {
                        continue;
                    }

                    var s = segments[idx];
                    Point2d other;
                    if (PointsEqual(s.A, end))
                    {
                        other = s.B;
                    }
                    else if (PointsEqual(s.B, end))
                    {
                        other = s.A;
                    }
                    else
                    {
                        continue;
                    }

                    unused.Remove(idx);
                    if (append)
                    {
                        path.Add(other);
                    }
                    else
                    {
                        path.Insert(0, other);
                    }

                    end = other;
                    return true;
                }

                return false;
            }
        }

        return result;
    }

    private static (long, long, long, long) EdgeKey(Point2d a, Point2d b)
    {
        var ka = PointKey(a);
        var kb = PointKey(b);
        return ka.Item1 < kb.Item1 || (ka.Item1 == kb.Item1 && ka.Item2 <= kb.Item2)
            ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2)
            : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
    }

    private static (long, long) PointKey(Point2d p) =>
        ((long)Math.Round(p.X / EdgeTol), (long)Math.Round(p.Y / EdgeTol));

    private static bool PointsEqual(Point2d a, Point2d b) =>
        PointKey(a) == PointKey(b);
}
