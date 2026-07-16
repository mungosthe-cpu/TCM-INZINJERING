using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData za Civil-like border poliliniju TCM terena.</summary>
internal static class TerrainBorderXData
{
    public const string RoleTerrainBorder = "TERB";

    public static void Attach(Entity entity, string? surfaceName = null)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTerrainBorder));

        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
        }

        entity.XData = buffer;
    }

    public static bool IsTerrainBorder(Entity entity)
    {
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        foreach (var item in values.AsArray())
        {
            if (item.TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                string.Equals(Convert.ToString(item.Value), RoleTerrainBorder, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetSurfaceName(Entity entity, out string? surfaceName)
    {
        surfaceName = null;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var sawRole = false;
        foreach (var item in values.AsArray())
        {
            if (item.TypeCode != (int)DxfCode.ExtendedDataAsciiString)
            {
                continue;
            }

            var s = Convert.ToString(item.Value);
            if (!sawRole)
            {
                if (string.Equals(s, RoleTerrainBorder, StringComparison.Ordinal))
                {
                    sawRole = true;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(s))
            {
                surfaceName = s.Trim();
                return true;
            }
        }

        return sawRole;
    }
}
