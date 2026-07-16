using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Širine traka kolovoza po osi (levo / desno). Dok nije definisan kolovoz — rubrika ostaje prazna.
/// </summary>
internal static class ProfileLaneWidthStore
{
    public static bool TryGetAtStation(
        string axisName,
        double station,
        out double leftWidth,
        out double rightWidth)
    {
        leftWidth = 0;
        rightWidth = 0;
        _ = axisName;
        _ = station;
        return false;
    }
}

/// <summary>
/// Kote projektovane nivelete (vertikalni profil). Dok VA nije snimljena — nema uzorka.
/// </summary>
internal static class ProfileGradeSampler
{
    public static bool TryLoadSamples(
        Transaction? tr,
        Database? db,
        string axisName,
        out List<(double Station, double Elevation)> samples)
    {
        samples = [];
        _ = tr;
        _ = db;
        _ = axisName;
        return false;
    }
}
