namespace TcmInzenjering.Plugin.Roads.CrossAxis;

public sealed class CrossAxisPlacementSettings
{
    /// <summary>
    /// Default kao na crtežu: "OSA 10" i "0-180.00" jedan iznad drugog,
    /// na istoj strani štapića (van osovine).
    /// </summary>
    public CrossAxisOffsetSettings Labels { get; set; } = new()
    {
        Side = CrossAxisSide.Right,
        OffsetX = 10.0,
        OffsetY = 1.7
    };

    public CrossAxisOffsetSettings Stations { get; set; } = new()
    {
        Side = CrossAxisSide.Right,
        OffsetX = 10.0,
        OffsetY = -1.7
    };

    public CrossAxisPlacementSettings Clone() => new()
    {
        Labels = Labels.Clone(),
        Stations = Stations.Clone()
    };
}
