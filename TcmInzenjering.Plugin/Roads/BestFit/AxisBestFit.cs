using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.BestFit;

internal sealed class AxisBestFitOptions
{
    public double MaxDeviation { get; init; } = 0.50;
    public int MinSamplesPerRun { get; init; } = 2;
}

/// <summary>
/// Split-and-merge orthogonal fit. The result is a clean PI polygon which can
/// be passed to the existing tangent/arc converter.
/// </summary>
internal static class AxisBestFit
{
    public static IReadOnlyList<Point2d> FitFromPolyline(
        Polyline source,
        AxisBestFitOptions options)
    {
        var samples = new List<Point2d>(source.NumberOfVertices);
        for (var index = 0; index < source.NumberOfVertices; index++)
        {
            var point = source.GetPoint2dAt(index);
            if (samples.Count == 0 || samples[^1].GetDistanceTo(point) > 1e-6)
            {
                samples.Add(point);
            }
        }

        return FitVertices(samples, options);
    }

    public static IReadOnlyList<Point2d> FitVertices(
        IReadOnlyList<Point2d> samples,
        AxisBestFitOptions options)
    {
        if (samples.Count < 2)
        {
            throw new InvalidOperationException("Best Fit zahteva najmanje dve različite tačke.");
        }

        var ranges = new List<(int Start, int End)>();
        Split(samples, 0, samples.Count - 1, options, ranges);
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));

        var lines = ranges.Select(range => FitLine(samples, range.Start, range.End)).ToList();
        if (lines.Count == 1)
        {
            return new[]
            {
                Project(samples[0], lines[0]),
                Project(samples[^1], lines[0])
            };
        }

        var vertices = new List<Point2d> { Project(samples[0], lines[0]) };
        for (var index = 0; index < lines.Count - 1; index++)
        {
            var left = lines[index];
            var right = lines[index + 1];
            var leftA = left.Center - left.Direction;
            var leftB = left.Center + left.Direction;
            var rightA = right.Center - right.Direction;
            var rightB = right.Center + right.Direction;
            if (!TangentArcGeometry.TryIntersectLines(leftA, leftB, rightA, rightB, out var pi))
            {
                pi = Midpoint(samples[ranges[index].End], samples[ranges[index + 1].Start]);
            }

            var localSpan = samples[ranges[index].Start].GetDistanceTo(samples[ranges[index].End]) +
                            samples[ranges[index + 1].Start].GetDistanceTo(samples[ranges[index + 1].End]);
            var joint = samples[ranges[index].End];
            if (pi.GetDistanceTo(joint) > Math.Max(10.0, localSpan * 2.0))
            {
                pi = joint;
            }

            if (vertices[^1].GetDistanceTo(pi) > 0.01)
            {
                vertices.Add(pi);
            }
        }

        var final = Project(samples[^1], lines[^1]);
        if (vertices[^1].GetDistanceTo(final) > 0.01)
        {
            vertices.Add(final);
        }

        return vertices;
    }

    private static void Split(
        IReadOnlyList<Point2d> samples,
        int start,
        int end,
        AxisBestFitOptions options,
        List<(int Start, int End)> ranges)
    {
        var line = FitLine(samples, start, end);
        var maxDistance = 0.0;
        var splitIndex = -1;
        for (var index = start + 1; index < end; index++)
        {
            var distance = PerpendicularDistance(samples[index], line);
            if (distance > maxDistance)
            {
                maxDistance = distance;
                splitIndex = index;
            }
        }

        var minimum = Math.Max(2, options.MinSamplesPerRun);
        if (maxDistance > Math.Max(0.01, options.MaxDeviation) &&
            splitIndex - start + 1 >= minimum &&
            end - splitIndex + 1 >= minimum)
        {
            Split(samples, start, splitIndex, options, ranges);
            Split(samples, splitIndex, end, options, ranges);
            return;
        }

        ranges.Add((start, end));
    }

    private static FittedLine FitLine(IReadOnlyList<Point2d> samples, int start, int end)
    {
        var count = end - start + 1;
        var centerX = 0.0;
        var centerY = 0.0;
        for (var index = start; index <= end; index++)
        {
            centerX += samples[index].X;
            centerY += samples[index].Y;
        }

        var center = new Point2d(centerX / count, centerY / count);
        var xx = 0.0;
        var xy = 0.0;
        var yy = 0.0;
        for (var index = start; index <= end; index++)
        {
            var dx = samples[index].X - center.X;
            var dy = samples[index].Y - center.Y;
            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }

        var angle = 0.5 * Math.Atan2(2.0 * xy, xx - yy);
        var direction = new Vector2d(Math.Cos(angle), Math.Sin(angle));
        var sourceDirection = samples[end] - samples[start];
        if (direction.DotProduct(sourceDirection) < 0)
        {
            direction = direction.Negate();
        }

        return new FittedLine(center, direction.GetNormal());
    }

    private static Point2d Project(Point2d point, FittedLine line)
    {
        var delta = point - line.Center;
        return line.Center + line.Direction * delta.DotProduct(line.Direction);
    }

    private static double PerpendicularDistance(Point2d point, FittedLine line)
    {
        var delta = point - line.Center;
        var cross = delta.X * line.Direction.Y - delta.Y * line.Direction.X;
        return Math.Abs(cross);
    }

    private static Point2d Midpoint(Point2d left, Point2d right) =>
        new((left.X + right.X) * 0.5, (left.Y + right.Y) * 0.5);

    private readonly struct FittedLine
    {
        public FittedLine(Point2d center, Vector2d direction)
        {
            Center = center;
            Direction = direction;
        }

        public Point2d Center { get; }
        public Vector2d Direction { get; }
    }
}
