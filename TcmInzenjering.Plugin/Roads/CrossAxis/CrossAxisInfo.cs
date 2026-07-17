namespace TcmInzenjering.Plugin.Roads.CrossAxis;

public sealed class CrossAxisInfo
{
    public int Number { get; init; }
    public long Handle { get; init; }
    public double Station { get; init; }
    public string RoadAxisName { get; init; } = string.Empty;
    public string DisplayName => $"STA {Number}";
    public string StationDisplay => Station.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}