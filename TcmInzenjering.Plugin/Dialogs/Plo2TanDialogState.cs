using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public enum Plo2TanDialogCloseAction
{
    Cancelled,
    Confirmed,
    PickStartStation,
    PickEndStation
}

public sealed class Plo2TanDialogState
{
    public string AxisName { get; set; } = "OSA-1";
    public double CurveRadius { get; set; } = 50;
    public double StartStation { get; set; }
    public double EndStation { get; set; }
    public double Interval { get; set; } = 20;
    public double TextHeight { get; set; } = 2.5;
    public double TickLength { get; set; } = RoadDrawing.DefaultTickLength;
    public string Prefix { get; set; } = "STA ";
    public int AxisCounterStart { get; set; } = 1;
    public StationLabelFormat LabelFormat { get; set; } = StationLabelFormat.ProjectCounter;
    public int ChainageFormat { get; set; } = ChainageFormatter.DefaultFormat;
    public bool DrawSegmentLabels { get; set; } = true;
    public bool EqualIntervalInBounds { get; set; } = true;
    public bool WholeInterval { get; set; } = true;
    public bool AlignToStart { get; set; } = true;
    public bool LabelAtStart { get; set; }
    public bool LabelAtEnd { get; set; } = true;
    public bool LabelAtMainPoints { get; set; }
    public short AxisColorIndex { get; set; } = DrawingColorDefaults.Axis;
    public short StationTextColorIndex { get; set; } = DrawingColorDefaults.StationText;
    public short StationTickColorIndex { get; set; } = DrawingColorDefaults.StationTick;
    public short SegmentLabelColorIndex { get; set; } = DrawingColorDefaults.SegmentLabel;

    public StationLabelOptions ToStationOptions() => new()
    {
        EqualIntervalInBounds = EqualIntervalInBounds,
        WholeInterval = WholeInterval,
        StartStation = StartStation,
        EndStation = EndStation,
        AlignToStart = AlignToStart,
        LabelAtStart = LabelAtStart,
        LabelAtEnd = LabelAtEnd,
        LabelAtMainPoints = LabelAtMainPoints,
        Interval = Interval,
        Prefix = Prefix,
        TextHeight = TextHeight,
        TickLength = TickLength,
        AxisCounterStart = AxisCounterStart,
        LabelFormat = LabelFormat,
        ChainageFormat = ChainageFormat,
        DrawSegmentLabels = DrawSegmentLabels,
        AxisColorIndex = AxisColorIndex,
        StationTextColorIndex = StationTextColorIndex,
        StationTickColorIndex = StationTickColorIndex,
        SegmentLabelColorIndex = SegmentLabelColorIndex
    };
}
