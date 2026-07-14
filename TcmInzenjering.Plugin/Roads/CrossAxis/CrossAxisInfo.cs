namespace TcmInzenjering.Plugin.Roads.CrossAxis;

public sealed class CrossAxisInfo
{
    public int Number { get; init; }
    public long Handle { get; init; }
    public string DisplayName => $"STA {Number}";
}
