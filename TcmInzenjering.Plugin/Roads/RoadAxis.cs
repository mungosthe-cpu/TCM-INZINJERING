using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal sealed class RoadAxis
{
    public string Name { get; init; } = "OS-1";
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
        foreach (var element in Elements)
        {
            if (station < element.StartStation - 1e-6 || station > element.EndStation + 1e-6)
            {
                continue;
            }

            if (element.Type == AlignmentElementType.Tangent)
            {
                var direction = element.End - element.Start;
                return direction.Length < 1e-9 ? null : direction.GetNormal();
            }

            var point = GetPointAtStation(station);
            if (point is null)
            {
                return null;
            }

            var radial = point.Value - element.Center;
            var tangent = element.Clockwise
                ? new Vector3d(radial.Y, -radial.X, 0)
                : new Vector3d(-radial.Y, radial.X, 0);

            return tangent.GetNormal();
        }

        return null;
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
