using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Vertikalni profil (niveleta) — kote po stacionaži.
/// Za sada nema namenskog TCMa; kad se doda projektovana niveleta, puni ovaj sampler.
/// </summary>
internal static class ProfileDesignGradeSampler
{
    public static bool TryLoadSamples(
        Transaction tr,
        Database db,
        string axisName,
        out List<(double Station, double Elevation)> samples)
    {
        samples = [];
        // TODO: učitati projektovanu niveletu kada postoji (vertikalni profil / TCLNV…).
        _ = tr;
        _ = db;
        _ = axisName;
        return false;
    }
}

/// <summary>
/// Širine kolovoza levo/desno po stacionaži (jedna rubrika, dve vrednosti).
/// Punice se kad se definišu širine traka u projektu.
/// </summary>
internal static class ProfileLaneWidthSampler
{
    public static bool TryGetWidths(
        Transaction tr,
        Database db,
        string axisName,
        double station,
        out double leftWidth,
        out double rightWidth)
    {
        leftWidth = 0;
        rightWidth = 0;
        _ = tr;
        _ = db;
        _ = axisName;
        _ = station;
        return false;
    }
}
