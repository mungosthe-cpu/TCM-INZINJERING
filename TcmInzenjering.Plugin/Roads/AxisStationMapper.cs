using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;
internal static class AxisStationMapper
{
    public static double MapPolylineDistanceToAxisStation(Polyline polyline, RoadAxis axis, double distanceAlongPolyline)
    {
        if (axis.Elements.Count == 0)
        {
            return 0;
        }

        var axisStart = axis.StartStation;
        var axisEnd = axis.Elements[^1].EndStation;
        var axisLength = axisEnd - axisStart;
        if (axisLength < 1e-9 || polyline.Length < 1e-9)
        {
            return axisStart;
        }

        var clamped = Math.Max(0, Math.Min(distanceAlongPolyline, polyline.Length));
        var fraction = clamped / polyline.Length;
        return axisStart + fraction * axisLength;
    }

    public static StationLabelOptions MapLabelOptionsToAxis(
        Polyline polyline,
        RoadAxis axis,
        StationLabelOptions options)
    {
        if (axis.Elements.Count == 0)
        {
            return options;
        }

        var axisEnd = axis.Elements[^1].EndStation;
        var mappedStart = MapPolylineDistanceToAxisStation(polyline, axis, options.StartStation);
        var mappedEnd = MapPolylineDistanceToAxisStation(polyline, axis, options.EndStation);
        mappedStart = Math.Max(axis.StartStation, Math.Min(mappedStart, axisEnd));
        mappedEnd = Math.Max(mappedStart, Math.Min(mappedEnd, axisEnd));

        return new StationLabelOptions
        {
            EqualIntervalInBounds = options.EqualIntervalInBounds,
            WholeInterval = options.WholeInterval,
            StartStation = mappedStart,
            EndStation = mappedEnd,
            AlignToStart = options.AlignToStart,
            LabelAtStart = options.LabelAtStart,
            LabelAtEnd = options.LabelAtEnd,
            LabelAtMainPoints = options.LabelAtMainPoints,
            Interval = options.Interval,
            Prefix = options.Prefix,
            TextHeight = options.TextHeight,
            TickLength = options.TickLength,
            LabelSideSign = options.LabelSideSign,
            AxisCounterStart = options.AxisCounterStart,
            LabelFormat = options.LabelFormat,
            ChainageFormat = options.ChainageFormat,
            DrawSegmentLabels = options.DrawSegmentLabels,
            AxisColorIndex = options.AxisColorIndex,
            StationTextColorIndex = options.StationTextColorIndex,
            StationTickColorIndex = options.StationTickColorIndex,
            SegmentLabelColorIndex = options.SegmentLabelColorIndex
        };
    }
}
