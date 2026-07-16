using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData marker za 3DFACE TCM terena (+ opciono ime surface-a).</summary>
internal static class TerrainFaceXData
{
    public const string RoleTerrainFace = "TERF";

    public static void Attach(Entity entity, string? surfaceName = null)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTerrainFace));

        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
        }

        entity.XData = buffer;
    }

    public static bool IsTerrainFace(Entity entity)
    {
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                string.Equals(Convert.ToString(items[i].Value), RoleTerrainFace, StringComparison.Ordinal))
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

        var items = values.AsArray();
        var sawRole = false;
        foreach (var item in items)
        {
            if (item.TypeCode != (int)DxfCode.ExtendedDataAsciiString)
            {
                continue;
            }

            var s = Convert.ToString(item.Value);
            if (!sawRole)
            {
                if (string.Equals(s, RoleTerrainFace, StringComparison.Ordinal))
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
