using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class PolylineToTangentConverter
{
    private const double CollinearToleranceDeg = 2.0;
    private const double MinSegmentLength = 0.01;

    public static RoadAxis Convert(
        Polyline source,
        double curveRadius,
        double startStation,
        string axisName)
    {
        var vertices = ExtractVertices(source);
        if (vertices.Count < 2)
        {
            throw new InvalidOperationException("Polylinija mora imati najmanje 2 temena.");
        }

        vertices = SimplifyCollinearVertices(vertices);
        var elements = BuildElements(vertices, curveRadius);
        AssignStations(elements, startStation);

        return new RoadAxis
        {
            Name = axisName,
            StartStation = startStation,
            Elements = elements
        };
    }

    private static List<Point2d> ExtractVertices(Polyline polyline)
    {
        var points = new List<Point2d>(polyline.NumberOfVertices);
        for (var i = 0; i < polyline.NumberOfVertices; i++)
        {
            points.Add(polyline.GetPoint2dAt(i));
        }

        return points;
    }

    private static List<Point2d> SimplifyCollinearVertices(IReadOnlyList<Point2d> vertices)
    {
        if (vertices.Count <= 2)
        {
            return vertices.ToList();
        }

        var simplified = new List<Point2d> { vertices[0] };
        for (var i = 1; i < vertices.Count - 1; i++)
        {
            var prev = simplified[^1];
            var current = vertices[i];
            var next = vertices[i + 1];

            if (!IsCollinear(prev, current, next, CollinearToleranceDeg))
            {
                simplified.Add(current);
            }
        }

        simplified.Add(vertices[^1]);
        return simplified;
    }

    private static bool IsCollinear(Point2d a, Point2d b, Point2d c, double toleranceDeg)
    {
        var v1 = new Vector2d(b.X - a.X, b.Y - a.Y);
        var v2 = new Vector2d(c.X - b.X, c.Y - b.Y);
        if (v1.Length < MinSegmentLength || v2.Length < MinSegmentLength)
        {
            return true;
        }

        var angle = Math.Abs(v1.GetAngleTo(v2) * 180.0 / Math.PI);
        return angle < toleranceDeg || Math.Abs(angle - 180.0) < toleranceDeg;
    }

    private static List<AlignmentElement> BuildElements(IReadOnlyList<Point2d> vertices, double requestedRadius)
    {
        var elements = new List<AlignmentElement>();

        if (vertices.Count == 2)
        {
            elements.Add(CreateTangent(vertices[0], vertices[1]));
            return elements;
        }

        var currentStart = To3d(vertices[0]);

        for (var i = 1; i < vertices.Count - 1; i++)
        {
            var previous = vertices[i - 1];
            var corner = vertices[i];
            var next = vertices[i + 1];

            var inVec = new Vector2d(corner.X - previous.X, corner.Y - previous.Y);
            var outVec = new Vector2d(next.X - corner.X, next.Y - corner.Y);
            if (inVec.Length < MinSegmentLength || outVec.Length < MinSegmentLength)
            {
                continue;
            }

            var inDir = inVec.GetNormal();
            var outDir = outVec.GetNormal();
            var dot = Math.Clamp(inDir.DotProduct(outDir), -1.0, 1.0);
            var deflection = Math.Acos(dot);
            var cross = inDir.X * outDir.Y - inDir.Y * outDir.X;
            if (Math.Abs(deflection) < 0.001 || Math.Abs(Math.Abs(deflection) - Math.PI) < 0.001)
            {
                continue;
            }

            var radius = Math.Max(requestedRadius, MinSegmentLength);
            var tangentLength = radius * Math.Tan(deflection / 2.0);
            tangentLength = Math.Min(tangentLength, inVec.Length * 0.45);
            tangentLength = Math.Min(tangentLength, outVec.Length * 0.45);

            if (tangentLength < MinSegmentLength)
            {
                continue;
            }

            radius = tangentLength / Math.Tan(deflection / 2.0);

            var arcStart = new Point2d(corner.X - inDir.X * tangentLength, corner.Y - inDir.Y * tangentLength);
            var arcEnd = new Point2d(corner.X + outDir.X * tangentLength, corner.Y + outDir.Y * tangentLength);

            if (currentStart.DistanceTo(To3d(arcStart)) > MinSegmentLength)
            {
                elements.Add(CreateTangent(currentStart, To3d(arcStart)));
            }

            elements.Add(CreateArc(arcStart, arcEnd, radius, cross < 0));
            currentStart = To3d(arcEnd);
        }

        var finalPoint = To3d(vertices[^1]);
        if (currentStart.DistanceTo(finalPoint) > MinSegmentLength)
        {
            elements.Add(CreateTangent(currentStart, finalPoint));
        }

        if (elements.Count == 0)
        {
            elements.Add(CreateTangent(To3d(vertices[0]), To3d(vertices[^1])));
        }

        return elements;
    }

    private static AlignmentElement CreateTangent(Point3d start, Point3d end)
    {
        return new AlignmentElement
        {
            Type = AlignmentElementType.Tangent,
            Start = start,
            End = end,
            Length = start.DistanceTo(end),
            Radius = 0,
            Center = Point3d.Origin,
            Clockwise = false
        };
    }

    private static AlignmentElement CreateTangent(Point2d start, Point2d end) =>
        CreateTangent(To3d(start), To3d(end));

    private static AlignmentElement CreateArc(Point2d start, Point2d end, double radius, bool clockwise)
    {
        var start3d = To3d(start);
        var end3d = To3d(end);
        var center = ComputeArcCenter(start, end, radius, clockwise);
        var length = radius * ComputeSweepAngle(start, end, center, clockwise);

        return new AlignmentElement
        {
            Type = AlignmentElementType.Arc,
            Start = start3d,
            End = end3d,
            Length = length,
            Radius = radius,
            Center = To3d(center),
            Clockwise = clockwise
        };
    }

    private static Point2d ComputeArcCenter(Point2d start, Point2d end, double radius, bool clockwise)
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

    private static void AssignStations(List<AlignmentElement> elements, double startStation)
    {
        var station = startStation;
        foreach (var element in elements)
        {
            element.StartStation = station;
            station += element.Length;
            element.EndStation = station;
        }
    }

    private static Point3d To3d(Point2d point) => new(point.X, point.Y, 0);
}
