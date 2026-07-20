using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal sealed class RoadAxis
{
    public string Name { get; init; } = "OSA-1";
    public double StartStation { get; init; }
    public IReadOnlyList<AlignmentElement> Elements { get; init; } = Array.Empty<AlignmentElement>();

    public double TotalLength => Elements.Count == 0 ? 0 : Elements[^1].EndStation - StartStation;

    public Point3d? GetPointAtStation(double station)
    {
        foreach (var element in Elements)
        {
            if (station < element.StartStation - 1e-6 || station > element.EndStation + 1e-6)
            {
                continue;
            }

            var offset = station - element.StartStation;
            if (element.Type == AlignmentElementType.Tangent)
            {
                var direction = element.End - element.Start;
                if (direction.Length < 1e-9)
                {
                    return element.Start;
                }

                direction = direction.GetNormal();
                return element.Start + direction * offset;
            }

            if (element.Type == AlignmentElementType.Spiral)
            {
                return SampleSpiral(element, offset);
            }

            var sweep = element.EndStation - element.StartStation;
            if (Math.Abs(sweep) < 1e-9 || Math.Abs(element.Radius) < 1e-9)
            {
                return element.Start;
            }

            var startVec = element.Start - element.Center;
            var endVec = element.End - element.Center;
            var startAngle = Math.Atan2(startVec.Y, startVec.X);
            var endAngle = Math.Atan2(endVec.Y, endVec.X);
            var ratio = offset / sweep;
            var angle = InterpolateAngle(startAngle, endAngle, element.Clockwise, ratio);
            return new Point3d(
                element.Center.X + element.Radius * Math.Cos(angle),
                element.Center.Y + element.Radius * Math.Sin(angle),
                element.Start.Z);
        }

        return null;
    }

    public Vector3d? GetDirectionAtStation(double station)
    {
        const double tolerance = 1e-3;
        AlignmentElement? match = null;

        for (var i = 0; i < Elements.Count; i++)
        {
            var element = Elements[i];
            if (station < element.StartStation - tolerance || station > element.EndStation + tolerance)
            {
                continue;
            }

            match = element;

            var atEnd = Math.Abs(station - element.EndStation) <= tolerance;
            var hasNext = i < Elements.Count - 1;
            if (atEnd && hasNext &&
                Math.Abs(station - Elements[i + 1].StartStation) <= tolerance &&
                station > element.StartStation + tolerance)
            {
                continue;
            }

            break;
        }

        if (match is null)
        {
            return null;
        }

        if (match.Type == AlignmentElementType.Tangent)
        {
            var direction = match.End - match.Start;
            return direction.Length < 1e-9 ? null : direction.GetNormal();
        }

        if (match.Type == AlignmentElementType.Spiral)
        {
            return SampleSpiralDirection(match, station - match.StartStation);
        }

        var point = GetPointAtStation(station);
        if (point is null)
        {
            return null;
        }

        var radial = point.Value - match.Center;
        var tangent = match.Clockwise
            ? new Vector3d(radial.Y, -radial.X, 0)
            : new Vector3d(-radial.Y, radial.X, 0);

        return tangent.GetNormal();
    }

    public AlignmentElement? GetElementAtStation(double station)
    {
        const double tolerance = 1e-3;
        AlignmentElement? match = null;

        for (var i = 0; i < Elements.Count; i++)
        {
            var element = Elements[i];
            if (station < element.StartStation - tolerance || station > element.EndStation + tolerance)
            {
                continue;
            }

            match = element;

            var atEnd = Math.Abs(station - element.EndStation) <= tolerance;
            var hasNext = i < Elements.Count - 1;
            if (atEnd && hasNext &&
                Math.Abs(station - Elements[i + 1].StartStation) <= tolerance &&
                station > element.StartStation + tolerance)
            {
                match = Elements[i + 1];
            }

            break;
        }

        return match;
    }

    public Vector3d? SampleDirectionAtStation(double station, double delta = 0.5)
    {
        if (Elements.Count == 0)
        {
            return null;
        }

        var axisEnd = Elements[^1].EndStation;
        var start = Math.Max(StartStation, station - delta);
        var end = Math.Min(axisEnd, station + delta);
        if (end - start < 1e-6)
        {
            return GetDirectionAtStation(station);
        }

        var startPoint = GetPointAtStation(start);
        var endPoint = GetPointAtStation(end);
        if (startPoint is null || endPoint is null)
        {
            return GetDirectionAtStation(station);
        }

        var direction = endPoint.Value - startPoint.Value;
        return direction.Length < 1e-9 ? GetDirectionAtStation(station) : direction.GetNormal();
    }

    private static double InterpolateAngle(double start, double end, bool clockwise, double ratio)
    {
        start = NormalizeAngle(start);
        end = NormalizeAngle(end);

        if (clockwise)
        {
            if (end > start)
            {
                end -= Math.PI * 2;
            }

            return start + (end - start) * ratio;
        }

        if (end < start)
        {
            end += Math.PI * 2;
        }

        return start + (end - start) * ratio;
    }

    private static Point3d SampleSpiral(AlignmentElement element, double offsetAlong)
    {
        var pts = element.SpiralPoints;
        if (pts is null || pts.Count == 0)
        {
            return element.Start;
        }

        if (pts.Count == 1 || offsetAlong <= 0)
        {
            return pts[0];
        }

        var target = Math.Min(offsetAlong, element.Length);
        var walked = 0.0;
        for (var i = 1; i < pts.Count; i++)
        {
            var seg = pts[i - 1].DistanceTo(pts[i]);
            if (walked + seg >= target - 1e-9)
            {
                var t = seg < 1e-12 ? 0 : (target - walked) / seg;
                return pts[i - 1] + (pts[i] - pts[i - 1]) * t;
            }

            walked += seg;
        }

        return pts[^1];
    }

    private static Vector3d? SampleSpiralDirection(AlignmentElement element, double offsetAlong)
    {
        var pts = element.SpiralPoints;
        if (pts is null || pts.Count < 2)
        {
            var d = element.End - element.Start;
            return d.Length < 1e-9 ? null : d.GetNormal();
        }

        var target = Math.Min(Math.Max(0, offsetAlong), element.Length);
        var walked = 0.0;
        for (var i = 1; i < pts.Count; i++)
        {
            var seg = pts[i - 1].DistanceTo(pts[i]);
            if (walked + seg >= target - 1e-9)
            {
                var d = pts[i] - pts[i - 1];
                return d.Length < 1e-9 ? null : d.GetNormal();
            }

            walked += seg;
        }

        var last = pts[^1] - pts[^2];
        return last.Length < 1e-9 ? null : last.GetNormal();
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle < 0)
        {
            angle += Math.PI * 2;
        }

        while (angle >= Math.PI * 2)
        {
            angle -= Math.PI * 2;
        }

        return angle;
    }
}
