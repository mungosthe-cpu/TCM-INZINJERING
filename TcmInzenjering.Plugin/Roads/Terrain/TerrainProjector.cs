using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

public enum TerrainSamplingMode
{
    /// <summary>Temena na preseima sa TIN/3DFACE ivicama + dodatna preciznost (broj tačaka).</summary>
    TerrainEdgeCrossings,

    /// <summary>Ravnomerna podela na N tačaka duž stacionaže (veći N = veća preciznost).</summary>
    FixedPointCount
}

public sealed class TerrainSamplingOptions
{
    public TerrainSamplingMode Mode { get; init; } = TerrainSamplingMode.FixedPointCount;

    /// <summary>Broj temena 3D polilinije (uključujući krajeve). Veći broj = bolje prati lukove.</summary>
    public int PointCount { get; init; } = 100;
}

internal sealed class TerrainProjectionResult
{
    public IReadOnlyList<Point3d> Points { get; init; } = Array.Empty<Point3d>();
    public int SampleCount { get; init; }
    public int HitCount { get; init; }
    public int MissCount { get; init; }
    public int EdgeCrossingCount { get; init; }
}

internal static class TerrainProjector
{
    private const double CrossingProbeStep = 1.0;

    public static TerrainProjectionResult ProjectRoadAxis(
        RoadAxis axis,
        TerrainElevationModel terrain,
        TerrainSamplingOptions options)
    {
        var stations = new SortedSet<double>();
        var crossingCount = 0;

        // Preciznost: uvek ravnomerne tačke po broju koji korisnik zada.
        foreach (var s in CollectFixedStations(axis, options.PointCount))
        {
            stations.Add(s);
        }

        if (options.Mode == TerrainSamplingMode.TerrainEdgeCrossings)
        {
            foreach (var s in CollectEdgeCrossingStations(axis, terrain, out crossingCount))
            {
                stations.Add(s);
            }
        }
        else
        {
            // PC/PT spojevi da se ne “preskoče” na spoju elemenata.
            foreach (var element in axis.Elements)
            {
                stations.Add(element.StartStation);
                stations.Add(element.EndStation);
            }
        }

        return ElevateStations(axis, terrain, stations.ToList(), crossingCount);
    }

    public static int CountTerrainEdgeCrossings(RoadAxis axis, TerrainElevationModel terrain)
    {
        CollectEdgeCrossingStations(axis, terrain, out var crossingsOnly);
        return crossingsOnly;
    }

    public static int EstimateStructureStationCount(RoadAxis axis)
    {
        var set = new SortedSet<double>();
        if (axis.Elements.Count == 0)
        {
            return 0;
        }

        set.Add(axis.StartStation);
        set.Add(axis.Elements[^1].EndStation);
        foreach (var element in axis.Elements)
        {
            set.Add(element.StartStation);
            set.Add(element.EndStation);
        }

        return set.Count;
    }

    /// <summary>Predlog broja tačaka (~1 tačka / m, min 50).</summary>
    public static int SuggestPointCount(RoadAxis axis)
    {
        if (axis.Elements.Count == 0)
        {
            return 50;
        }

        var length = Math.Abs(axis.Elements[^1].EndStation - axis.StartStation);
        return Math.Max(50, (int)Math.Ceiling(length) + 1);
    }

    private static List<double> CollectFixedStations(RoadAxis axis, int pointCount)
    {
        var stations = new List<double>();
        if (axis.Elements.Count == 0)
        {
            return stations;
        }

        var start = axis.StartStation;
        var end = axis.Elements[^1].EndStation;
        var n = Math.Max(2, pointCount);
        if (Math.Abs(end - start) < 1e-9)
        {
            stations.Add(start);
            return stations;
        }

        for (var i = 0; i < n; i++)
        {
            var t = i / (double)(n - 1);
            stations.Add(start + t * (end - start));
        }

        return stations;
    }

    private static List<double> CollectEdgeCrossingStations(
        RoadAxis axis,
        TerrainElevationModel terrain,
        out int crossingCount)
    {
        crossingCount = 0;
        var stations = new SortedSet<double>();
        if (axis.Elements.Count == 0)
        {
            return stations.ToList();
        }

        var start = axis.StartStation;
        var end = axis.Elements[^1].EndStation;
        stations.Add(start);
        stations.Add(end);
        foreach (var element in axis.Elements)
        {
            stations.Add(element.StartStation);
            stations.Add(element.EndStation);
        }

        var probe = BuildAxisProbe(axis, start, end, CrossingProbeStep, maxChordError: 0.02);
        var crossingStations = new SortedSet<double>();
        for (var i = 0; i < probe.Count - 1; i++)
        {
            var (s0, a) = probe[i];
            var (s1, b) = probe[i + 1];
            foreach (var (p, q) in terrain.EnumeratePlanEdgesNear(a, b))
            {
                if (!TryIntersectPlanSegments(a, b, p, q, out var t, out _))
                {
                    continue;
                }

                if (t <= 1e-6 || t >= 1.0 - 1e-6)
                {
                    continue;
                }

                crossingStations.Add(s0 + t * (s1 - s0));
            }
        }

        crossingCount = crossingStations.Count;
        foreach (var s in crossingStations)
        {
            stations.Add(s);
        }

        return stations.ToList();
    }

    private static List<(double Station, Point2d Point)> BuildAxisProbe(
        RoadAxis axis,
        double start,
        double end,
        double maxStep,
        double maxChordError)
    {
        var stations = new SortedSet<double> { start, end };
        foreach (var element in axis.Elements)
        {
            var a = Math.Max(start, element.StartStation);
            var b = Math.Min(end, element.EndStation);
            if (b - a < 1e-9)
            {
                continue;
            }

            stations.Add(a);
            stations.Add(b);

            var step = element.Type == AlignmentElementType.Tangent
                ? maxStep
                : Math.Min(maxStep, ArcStationStep(element.Radius, maxChordError));

            for (var s = a + step; s < b - 1e-9; s += step)
            {
                stations.Add(s);
            }
        }

        var probe = new List<(double Station, Point2d Point)>();
        foreach (var s in stations)
        {
            var p = axis.GetPointAtStation(s);
            if (p is null)
            {
                continue;
            }

            probe.Add((s, new Point2d(p.Value.X, p.Value.Y)));
        }

        return probe;
    }

    private static double ArcStationStep(double radius, double maxChordError)
    {
        var r = Math.Abs(radius);
        if (r < 1e-6)
        {
            return 1.0;
        }

        var m = Math.Min(maxChordError, r * 0.5);
        var ratio = Math.Clamp(1.0 - (m / r), -1.0, 1.0);
        var theta = 2.0 * Math.Acos(ratio);
        return Math.Clamp(r * theta, 0.05, 5.0);
    }

    private static TerrainProjectionResult ElevateStations(
        RoadAxis axis,
        TerrainElevationModel terrain,
        IReadOnlyList<double> stations,
        int edgeCrossingCount)
    {
        var points = new List<Point3d>(stations.Count);
        var hits = 0;
        var misses = 0;
        double? lastZ = null;

        foreach (var station in stations)
        {
            var p = axis.GetPointAtStation(station);
            if (p is null)
            {
                misses++;
                continue;
            }

            // XY sa plan-ose — polilinija prati osovinu; Z sa terena.
            if (terrain.TryGetElevation(p.Value.X, p.Value.Y, out var z))
            {
                points.Add(new Point3d(p.Value.X, p.Value.Y, z));
                lastZ = z;
                hits++;
            }
            else if (lastZ is not null)
            {
                points.Add(new Point3d(p.Value.X, p.Value.Y, lastZ.Value));
                misses++;
            }
            else
            {
                misses++;
            }
        }

        var cleaned = new List<Point3d>(points.Count);
        foreach (var p in points)
        {
            if (cleaned.Count == 0 || cleaned[^1].DistanceTo(p) > 1e-4)
            {
                cleaned.Add(p);
            }
        }

        return new TerrainProjectionResult
        {
            Points = cleaned,
            SampleCount = stations.Count,
            HitCount = hits,
            MissCount = misses,
            EdgeCrossingCount = edgeCrossingCount
        };
    }

    private static bool TryIntersectPlanSegments(
        Point2d a1,
        Point2d a2,
        Point2d b1,
        Point2d b2,
        out double tOnA,
        out Point2d intersection)
    {
        tOnA = 0;
        intersection = Point2d.Origin;

        var x1 = a1.X;
        var y1 = a1.Y;
        var x2 = a2.X;
        var y2 = a2.Y;
        var x3 = b1.X;
        var y3 = b1.Y;
        var x4 = b2.X;
        var y4 = b2.Y;

        var denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(denom) < 1e-12)
        {
            return false;
        }

        var t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denom;
        var u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / denom;
        if (t < 0 || t > 1 || u < 0 || u > 1)
        {
            return false;
        }

        tOnA = t;
        intersection = new Point2d(x1 + t * (x2 - x1), y1 + t * (y2 - y1));
        return true;
    }
}
