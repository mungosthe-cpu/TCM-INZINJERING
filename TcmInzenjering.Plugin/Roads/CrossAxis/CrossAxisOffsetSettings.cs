namespace TcmInzenjering.Plugin.Roads.CrossAxis;

public sealed class CrossAxisOffsetSettings
{
    public bool Enabled { get; set; } = true;
    public CrossAxisSide Side { get; set; } = CrossAxisSide.Right;
    public double OffsetX { get; set; } = 5.0;
    public double OffsetY { get; set; }

    public CrossAxisOffsetSettings Clone() => new()
    {
        Enabled = Enabled,
        Side = Side,
        OffsetX = OffsetX,
        OffsetY = OffsetY
    };
}
