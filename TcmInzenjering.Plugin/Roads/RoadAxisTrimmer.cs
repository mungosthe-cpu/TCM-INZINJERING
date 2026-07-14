using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class RoadAxisTrimmer
{
    public static RoadAxis Trim(RoadAxis axis, double startStation, double endStation)
    {
        if (axis.Elements.Count == 0)
        {
            return axis;
        }

        var axisStart = axis.StartStation;
        var axisEnd = axis.Elements[^1].EndStation;
        startStation = Math.Max(axisStart, startStation);
        endStation = Math.Min(axisEnd, endStation);

        if (endStation <= startStation + 1e-6)
        {
            return new RoadAxis
            {
                Name = axis.Name,
                StartStation = startStation,
                Elements = Array.Empty<AlignmentElement>()
            };
        }

        if (startStation <= axisStart + 1e-6 && endStation >= axisEnd - 1e-6)
        {
            return axis;
        }

        var trimmed = new List<AlignmentElement>();
        foreach (var element in axis.Elements)
        {
            if (element.EndStation < startStation - 1e-6 || element.StartStation > endStation + 1e-6)
            {
                continue;
            }

            var clipStart = Math.Max(startStation, element.StartStation);
            var clipEnd = Math.Min(endStation, element.EndStation);
            if (clipEnd <= clipStart + 1e-6)
            {
                continue;
            }

            var isFullElement =
                clipStart <= element.StartStation + 1e-6 &&
                clipEnd >= element.EndStation - 1e-6;

            var clipped = isFullElement
                ? CloneElement(element)
                : CreateClippedElement(axis, element, clipStart, clipEnd);

            if (clipped is not null)
            {
                trimmed.Add(clipped);
            }
        }

        return new RoadAxis
        {
            Name = axis.Name,
            StartStation = startStation,
            Elements = trimmed
        };
    }

    private static AlignmentElement CloneElement(AlignmentElement element) =>
        new()
        {
            Type = element.Type,
            Start = element.Start,
            End = element.End,
            Length = element.Length,
            StartStation = element.StartStation,
            EndStation = element.EndStation,
            Radius = element.Radius,
            Center = element.Center,
            Clockwise = element.Clockwise
        };

    private static AlignmentElement? CreateClippedElement(
        RoadAxis axis,
        AlignmentElement element,
        double clipStart,
        double clipEnd)
    {
        var startPoint = axis.GetPointAtStation(clipStart);
        var endPoint = axis.GetPointAtStation(clipEnd);
        if (startPoint is null || endPoint is null)
        {
            return null;
        }

        var length = clipEnd - clipStart;
        if (length < 1e-6)
        {
            return null;
        }

        if (element.Type == AlignmentElementType.Tangent)
        {
            return new AlignmentElement
            {
                Type = AlignmentElementType.Tangent,
                Start = startPoint.Value,
                End = endPoint.Value,
                Length = length,
                StartStation = clipStart,
                EndStation = clipEnd,
                Radius = 0,
                Center = Point3d.Origin,
                Clockwise = false
            };
        }

        return new AlignmentElement
        {
            Type = AlignmentElementType.Arc,
            Start = startPoint.Value,
            End = endPoint.Value,
            Length = length,
            StartStation = clipStart,
            EndStation = clipEnd,
            Radius = element.Radius,
            Center = element.Center,
            Clockwise = element.Clockwise
        };
    }
}
