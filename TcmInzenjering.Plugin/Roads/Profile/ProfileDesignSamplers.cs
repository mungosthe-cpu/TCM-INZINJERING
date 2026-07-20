using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Vertikalni profil (niveleta) — kote po stacionaži iz VerticalProfileStore.
/// </summary>
internal static class ProfileDesignGradeSampler
{
    public static bool TryLoadSamples(
        Transaction tr,
        Database db,
        string axisName,
        out List<(double Station, double Elevation)> samples) =>
        ProfileGradeSampler.TryLoadSamples(tr, db, axisName, out samples);
}

/// <summary>
/// Širine kolovoza levo/desno po stacionaži.
/// </summary>
internal static class ProfileLaneWidthSampler
{
    public static bool TryGetWidths(
        Transaction tr,
        Database db,
        string axisName,
        double station,
        out double leftWidth,
        out double rightWidth) =>
        ProfileLaneWidthStore.TryGetAtStation(tr, db, axisName, station, out leftWidth, out rightWidth);
}
