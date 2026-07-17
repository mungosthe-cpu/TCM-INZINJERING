using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Lejeri po imenovanom terenu: TCM_TEREN_Teren_1, TCM_IZO_MAJOR_Teren_1, …
/// </summary>
internal static class TerrainLayerNames
{
    private static readonly char[] InvalidLayerChars = ['<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`'];

    public static string Sanitize(string? surfaceName)
    {
        if (string.IsNullOrWhiteSpace(surfaceName))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(surfaceName.Trim().Length);
        foreach (var ch in surfaceName.Trim())
        {
            if (ch <= 32 || InvalidLayerChars.Contains(ch))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? string.Empty : s;
    }

    /// <summary>Bazni lejer ili bazni_Teren_1.</summary>
    public static string For(string baseLayer, string? surfaceName)
    {
        var suffix = Sanitize(surfaceName);
        return string.IsNullOrEmpty(suffix) ? baseLayer : $"{baseLayer}_{suffix}";
    }

    public static bool IsBaseOrPrefixed(string? layer, string baseLayer)
    {
        if (string.IsNullOrWhiteSpace(layer))
        {
            return false;
        }

        return string.Equals(layer, baseLayer, StringComparison.OrdinalIgnoreCase) ||
               layer.StartsWith(baseLayer + "_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesSurface(string? layer, string baseLayer, string? surfaceName)
    {
        if (string.IsNullOrWhiteSpace(surfaceName))
        {
            return IsBaseOrPrefixed(layer, baseLayer);
        }

        return string.Equals(layer, For(baseLayer, surfaceName), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Da li entitet pripada imenovanom terenu (XData + lejer).</summary>
internal static class TerrainSurfaceScope
{
    public static bool FaceBelongsTo(Entity entity, string? surfaceName)
    {
        if (string.IsNullOrWhiteSpace(surfaceName))
        {
            return TerrainFaceXData.IsTerrainFace(entity) ||
                   TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.TerrainLayerName);
        }

        if (TerrainFaceXData.TryGetSurfaceName(entity, out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return string.Equals(name, surfaceName, StringComparison.OrdinalIgnoreCase);
        }

        // Stariji crteži bez XData imena — lejer sa sufiksom ili aktivni baza lejer.
        return TerrainLayerNames.MatchesSurface(entity.Layer, RoadCommands.TerrainLayerName, surfaceName) ||
               (TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.TerrainLayerName) &&
                string.Equals(entity.Layer, RoadCommands.TerrainLayerName, StringComparison.OrdinalIgnoreCase));
    }

    public static bool BorderBelongsTo(Entity entity, string? surfaceName)
    {
        if (string.IsNullOrWhiteSpace(surfaceName))
        {
            return TerrainBorderXData.IsTerrainBorder(entity) ||
                   TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.TerrainBorderLayerName);
        }

        if (TerrainBorderXData.TryGetSurfaceName(entity, out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return string.Equals(name, surfaceName, StringComparison.OrdinalIgnoreCase);
        }

        return TerrainLayerNames.MatchesSurface(entity.Layer, RoadCommands.TerrainBorderLayerName, surfaceName);
    }

    public static bool ContourBelongsTo(Entity entity, string? surfaceName)
    {
        if (string.IsNullOrWhiteSpace(surfaceName))
        {
            return true;
        }

        if (TerrainContourXData.TryGetSurfaceName(entity, out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return string.Equals(name, surfaceName, StringComparison.OrdinalIgnoreCase);
        }

        return TerrainLayerNames.MatchesSurface(entity.Layer, RoadCommands.ContourMajorLayer, surfaceName) ||
               TerrainLayerNames.MatchesSurface(entity.Layer, RoadCommands.ContourMinorLayer, surfaceName) ||
               TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, "TCM_IZO_USER");
    }
}
