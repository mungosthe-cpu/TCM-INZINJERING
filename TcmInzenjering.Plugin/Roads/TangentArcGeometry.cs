using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class TangentArcGeometry
{
    public const double MinSegmentLength = 0.01;

    public static bool TryBuildCornerArc(
        Point2d corner,
        Vector2d inDir,
        Vector2d outDir,
        double requestedRadius,
        out Point2d arcStart,
        out Point2d arcEnd,
        out double radius,
        out bool clockwise,
        double? maxIncomingTangent = null,
        double? maxOutgoingTangent = null)
    {
        arcStart = corner;
        arcEnd = corner;
        radius = requestedRadius;
        clockwise = false;

        if (inDir.Length < MinSegmentLength || outDir.Length < MinSegmentLength)
        {
            return false;
        }

        inDir = inDir.GetNormal();
        outDir = outDir.GetNormal();

        var dot = MathNet48.Clamp(inDir.DotProduct(outDir), -1.0, 1.0);
        var deflection = Math.Acos(dot);
        var cross = inDir.X * outDir.Y - inDir.Y * outDir.X;
        if (Math.Abs(deflection) < 0.001 || Math.Abs(Math.Abs(deflection) - Math.PI) < 0.001)
        {
            return false;
        }

        radius = Math.Max(requestedRadius, MinSegmentLength);
        var tangentLength = radius * Math.Tan(deflection / 2.0);
        if (maxIncomingTangent is not null)
        {
            tangentLength = Math.Min(tangentLength, maxIncomingTangent.Value);
        }

        if (maxOutgoingTangent is not null)
        {
            tangentLength = Math.Min(tangentLength, maxOutgoingTangent.Value);
        }

        if (tangentLength < MinSegmentLength)
        {
            return false;
        }

        radius = tangentLength / Math.Tan(deflection / 2.0);
        arcStart = new Point2d(
            corner.X - inDir.X * tangentLength,
            corner.Y - inDir.Y * tangentLength);
        arcEnd = new Point2d(
            corner.X + outDir.X * tangentLength,
            corner.Y + outDir.Y * tangentLength);
        clockwise = cross < 0;
        return true;
    }

    public static AlignmentElement CreateArcElement(Point2d start, Point2d end, double radius, bool clockwise)
    {
        var center = ComputeArcCenter(start, end, radius, clockwise);
        var length = radius * ComputeSweepAngle(start, end, center, clockwise);

        return new AlignmentElement
        {
            Type = AlignmentElementType.Arc,
            Start = To3d(start),
            End = To3d(end),
            Length = length,
            Radius = radius,
            Center = To3d(center),
            Clockwise = clockwise
        };
    }

    public static bool TryIntersectLines(
        Point2d a1,
        Point2d a2,
        Point2d b1,
        Point2d b2,
        out Point2d intersection)
    {
        intersection = Point2d.Origin;
        var x1 = a1.X;
        var y1 = a1.Y;
        var x2 = a2.X;
        var y2 = a2.Y;
        var x3 = b1.X;
        var y3 = b1.Y;
        var x4 = b2.X;
        var y4 = b2.Y;

        var denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(denominator) < 1e-9)
        {
            return false;
        }

        var t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denominator;
        intersection = new Point2d(
            x1 + t * (x2 - x1),
            y1 + t * (y2 - y1));
        return true;
    }

    public static Point2d ComputeArcCenter(Point2d start, Point2d end, double radius, bool clockwise)
    {
        var mid = new Point2d((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
        var chord = new Vector2d(end.X - start.X, end.Y - start.Y);
        var chordLength = chord.Length;
        if (chordLength < MinSegmentLength)
        {
            return mid;
        }

        var halfChord = chordLength / 2.0;
        var height = Math.Sqrt(Math.Max(0, radius * radius - halfChord * halfChord));
        var normal = new Vector2d(-chord.Y / chordLength, chord.X / chordLength);
        if (clockwise)
        {
            normal = normal.Negate();
        }

        return new Point2d(mid.X + normal.X * height, mid.Y + normal.Y * height);
    }

    public static double ComputeSweepAngle(Point2d start, Point2d end, Point2d center, bool clockwise)
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

    public static Point2d To2d(Point3d point) => new(point.X, point.Y);

    public static Point3d To3d(Point2d point) => new(point.X, point.Y, 0);
}
