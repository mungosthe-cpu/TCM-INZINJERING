namespace TcmInzenjering.Plugin.Roads;

public enum StationLabelFormat
{
    ProjectCounter = 0,
    ChainageOnly = 1
}

public sealed class StationLabelOptions
{
    public bool EqualIntervalInBounds { get; init; } = true;
    public bool WholeInterval { get; init; } = true;
    public double StartStation { get; init; }
    public double EndStation { get; init; }
    public bool AlignToStart { get; init; } = true;
    public bool LabelAtStart { get; init; }
    public bool LabelAtEnd { get; init; } = true;
    public bool LabelAtMainPoints { get; init; }
    public double Interval { get; init; } = 20;
    public string Prefix { get; init; } = "STA ";
    public double TextHeight { get; init; } = 2.5;
    public double TickLength { get; init; } = RoadDrawing.DefaultTickLength;
    public double LabelSideSign { get; init; } = RoadDrawing.DefaultLabelSideSign;
    public int AxisCounterStart { get; init; } = 1;
    public StationLabelFormat LabelFormat { get; init; } = StationLabelFormat.ProjectCounter;
    public int ChainageFormat { get; init; } = ChainageFormatter.DefaultFormat;
    public bool DrawSegmentLabels { get; init; }
    public short AxisColorIndex { get; init; } = DrawingColorDefaults.Axis;
    public short StationTextColorIndex { get; init; } = DrawingColorDefaults.StationText;
    public short StationTickColorIndex { get; init; } = DrawingColorDefaults.StationTick;
    public short SegmentLabelColorIndex { get; init; } = DrawingColorDefaults.SegmentLabel;
}
