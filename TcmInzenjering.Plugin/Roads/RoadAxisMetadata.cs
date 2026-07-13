namespace TcmInzenjering.Plugin.Roads;

internal sealed class RoadAxisMetadata
{
    public string Name { get; init; } = "OS-1";
    public double StartStation { get; init; }
    public double EndStation { get; init; }
    public double Interval { get; init; } = 20;
    public double TickLength { get; init; } = 2.0;
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
        LabelSideSign = LabelSideSign
    };
}
