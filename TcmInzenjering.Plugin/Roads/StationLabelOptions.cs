namespace TcmInzenjering.Plugin.Roads;

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
    public double TickLength { get; init; } = 2.0;
    public double LabelSideSign { get; init; } = RoadDrawing.DefaultLabelSideSign;
}
