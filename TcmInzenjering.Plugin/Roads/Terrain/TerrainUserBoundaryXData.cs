using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData za korisničku granicu (TCMTERBOUND) — brisanje linije vraća TIN bez granice.</summary>
internal static class TerrainUserBoundaryXData
{
    public const string Role = "TERUBOUND";
    public const string LayerName = "TCM_TER_GRANICA";

    public static void Attach(Entity entity, TerrainBoundaryKind kind, string? surfaceName)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, Role),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind.ToString()),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName?.Trim() ?? string.Empty));
        entity.XData = buffer;
    }

    public static bool IsUserBoundary(Entity entity)
    {
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        foreach (var item in values.AsArray())
        {
            if (item.TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                string.Equals(Convert.ToString(item.Value), Role, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryRead(Entity entity, out TerrainBoundaryKind kind, out string? surfaceName)
    {
        kind = TerrainBoundaryKind.Outer;
        surfaceName = null;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        var sawRole = false;
        string? kindText = null;
        foreach (var item in items)
        {
            if (item.TypeCode != (int)DxfCode.ExtendedDataAsciiString)
            {
                continue;
            }

            var s = Convert.ToString(item.Value);
            if (!sawRole)
            {
                if (string.Equals(s, Role, StringComparison.Ordinal))
                {
                    sawRole = true;
                }

                continue;
            }

            if (kindText is null)
            {
                kindText = s;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(s))
            {
                surfaceName = s.Trim();
            }

            break;
        }

        if (!sawRole)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(kindText) &&
            Enum.TryParse(kindText, ignoreCase: true, out TerrainBoundaryKind parsed))
        {
            kind = parsed;
        }

        return true;
    }
}
