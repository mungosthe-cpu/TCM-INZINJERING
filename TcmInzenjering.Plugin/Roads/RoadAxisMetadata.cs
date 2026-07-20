using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

internal sealed class RoadAxisMetadata
{
    public string Name { get; init; } = "OSA-1";
    public double StartStation { get; init; }
    public double EndStation { get; init; }
    public double Interval { get; init; } = 20;
    public double TickLength { get; init; } = RoadDrawing.DefaultTickLength;
    public double TextHeight { get; init; } = 2.5;
    public string Prefix { get; init; } = "STA ";
    public double LabelSideSign { get; init; } = 1.0;
    public double CurveRadius { get; init; } = 50.0;
    public bool EqualIntervalInBounds { get; init; } = true;
    public bool WholeInterval { get; init; } = true;
    public bool AlignToStart { get; init; } = true;
    public bool LabelAtStart { get; init; }
    public bool LabelAtEnd { get; init; } = true;
    public bool LabelAtMainPoints { get; init; }
    public long SourcePolylineHandle { get; init; }
    public double PolylineStartDistance { get; init; }
    public double PolylineEndDistance { get; init; }
    public double PolylineReferenceLength { get; init; }
    public int AxisCounterStart { get; init; } = 1;
    public StationLabelFormat LabelFormat { get; init; } = StationLabelFormat.ProjectCounter;
    public int ChainageFormat { get; init; } = ChainageFormatter.DefaultFormat;
    public bool DrawSegmentLabels { get; init; }
    public short AxisColorIndex { get; init; } = DrawingColorDefaults.Axis;
    public short StationTextColorIndex { get; init; } = DrawingColorDefaults.StationText;
    public short StationTickColorIndex { get; init; } = DrawingColorDefaults.StationTick;
    public short SegmentLabelColorIndex { get; init; } = DrawingColorDefaults.SegmentLabel;

    public bool HasSourcePolyline => SourcePolylineHandle != 0;

    public StationLabelOptions ToLabelOptions() => new()
    {
        EqualIntervalInBounds = EqualIntervalInBounds,
        WholeInterval = WholeInterval,
        StartStation = StartStation,
        EndStation = EndStation > StartStation ? EndStation : StartStation,
        AlignToStart = AlignToStart,
        LabelAtStart = LabelAtStart,
        LabelAtEnd = LabelAtEnd,
        LabelAtMainPoints = LabelAtMainPoints,
        Interval = Interval,
        Prefix = Prefix,
        TextHeight = TextHeight,
        TickLength = TickLength,
        LabelSideSign = LabelSideSign,
        AxisCounterStart = AxisCounterStart,
        LabelFormat = LabelFormat,
        ChainageFormat = ChainageFormat,
        DrawSegmentLabels = DrawSegmentLabels,
        AxisColorIndex = AxisColorIndex,
        StationTextColorIndex = StationTextColorIndex,
        StationTickColorIndex = StationTickColorIndex,
        SegmentLabelColorIndex = SegmentLabelColorIndex
    };

    public StationLabelOptions ToLabelOptions(Polyline polyline, RoadAxis axis)
    {
        var (polylineStart, polylineEnd) = ResolvePolylineSpan(polyline);

        var options = new StationLabelOptions
        {
            EqualIntervalInBounds = EqualIntervalInBounds,
            WholeInterval = WholeInterval,
            StartStation = polylineStart,
            EndStation = polylineEnd,
            AlignToStart = AlignToStart,
            LabelAtStart = LabelAtStart,
            LabelAtEnd = LabelAtEnd,
            LabelAtMainPoints = LabelAtMainPoints,
            Interval = Interval,
            Prefix = Prefix,
            TextHeight = TextHeight,
            TickLength = TickLength,
            LabelSideSign = LabelSideSign,
            AxisCounterStart = AxisCounterStart,
            LabelFormat = LabelFormat,
            ChainageFormat = ChainageFormat,
            DrawSegmentLabels = DrawSegmentLabels,
            AxisColorIndex = AxisColorIndex,
            StationTextColorIndex = StationTextColorIndex,
            StationTickColorIndex = StationTickColorIndex,
            SegmentLabelColorIndex = SegmentLabelColorIndex
        };

        return HasSourcePolyline
            ? AxisStationMapper.MapLabelOptionsToAxis(polyline, axis, options)
            : options;
    }

    internal (double Start, double End) ResolvePolylineSpan(Polyline polyline)
    {
        const double tolerance = 1e-3;
        var start = Math.Max(0, Math.Min(PolylineStartDistance, polyline.Length));
        var end = Math.Max(start, Math.Min(PolylineEndDistance, polyline.Length));

        if (polyline.Length <= end + tolerance)
        {
            return (start, end);
        }

        var endWasAtPreviousTerminus = PolylineReferenceLength > tolerance &&
            Math.Abs(PolylineEndDistance - PolylineReferenceLength) < tolerance;
        var legacyFullInterval =
            WholeInterval &&
            start < tolerance &&
            (PolylineReferenceLength < tolerance ||
             Math.Abs(PolylineEndDistance - PolylineReferenceLength) < tolerance);

        if (endWasAtPreviousTerminus || legacyFullInterval)
        {
            end = polyline.Length;
        }

        return (start, end);
    }
}
