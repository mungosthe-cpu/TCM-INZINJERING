using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class ArcOrientation
{
    private const double Tolerance = 1e-3;

    public static AlignmentElement ReadArc(Arc arc) =>
        new()
        {
            Type = AlignmentElementType.Arc,
            Start = arc.StartPoint,
            End = arc.EndPoint,
            Length = arc.Length,
            Radius = arc.Radius,
            Center = arc.Center,
            Clockwise = false
        };

    public static void OrientArcsToChainage(IList<AlignmentElement> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            if (elements[i].Type != AlignmentElementType.Arc)
            {
                continue;
            }

            var previous = i > 0 ? elements[i - 1] : null;
            var next = i < elements.Count - 1 ? elements[i + 1] : null;
            elements[i] = OrientArc(elements[i], previous, next);
        }
    }

    private static AlignmentElement OrientArc(
        AlignmentElement arc,
        AlignmentElement? previous,
        AlignmentElement? next)
    {
        var start = arc.Start;
        var end = arc.End;

        if (ShouldFlip(start, end, previous?.End, next?.Start))
        {
            (start, end) = (end, start);
        }

        return new AlignmentElement
        {
            Type = AlignmentElementType.Arc,
            Start = start,
            End = end,
            Length = arc.Length,
            Radius = arc.Radius,
            Center = arc.Center,
            Clockwise = ComputeClockwise(start, end, arc.Center, arc.Length, arc.Radius)
        };
    }

    private static bool ShouldFlip(
        Point3d start,
        Point3d end,
        Point3d? expectedStart,
        Point3d? expectedEnd)
    {
        if (expectedStart is not null)
        {
            var forward = expectedStart.Value.DistanceTo(start);
            var flipped = expectedStart.Value.DistanceTo(end);
            if (forward <= Tolerance)
            {
                return false;
            }

            if (flipped <= Tolerance)
            {
                return true;
            }

            return flipped < forward;
        }

        if (expectedEnd is not null)
        {
            var forward = expectedEnd.Value.DistanceTo(end);
            var flipped = expectedEnd.Value.DistanceTo(start);
            if (forward <= Tolerance)
            {
                return false;
            }

            if (flipped <= Tolerance)
            {
                return true;
            }

            return flipped < forward;
        }

        return false;
    }

    private static bool ComputeClockwise(
        Point3d start,
        Point3d end,
        Point3d center,
        double arcLength,
        double radius)
    {
        if (radius < 1e-9)
        {
            return false;
        }

        var start2d = new Point2d(start.X, start.Y);
        var end2d = new Point2d(end.X, end.Y);
        var center2d = new Point2d(center.X, center.Y);
        var clockwiseSweep = ComputeSweepAngle(start2d, end2d, center2d, clockwise: true);
        var counterClockwiseSweep = ComputeSweepAngle(start2d, end2d, center2d, clockwise: false);
        var clockwiseLength = radius * clockwiseSweep;
        var counterClockwiseLength = radius * counterClockwiseSweep;

        return Math.Abs(clockwiseLength - arcLength) <= Math.Abs(counterClockwiseLength - arcLength);
    }

    private static double ComputeSweepAngle(Point2d start, Point2d end, Point2d center, bool clockwise)
    {
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);

        if (clockwise)
        {
            if (endAngle > startAngle)
            {
                endAngle -= Math.PI * 2;
            }

            return Math.Abs(endAngle - startAngle);
        }

        if (endAngle < startAngle)
        {
            endAngle += Math.PI * 2;
        }

        return Math.Abs(endAngle - startAngle);
    }
}
