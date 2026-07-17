using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData za slope strelice i watershed polilinije (+ opciono ime terena).</summary>
internal static class TerrainAnalysisXData
{
    public const string RoleSlopeArrow = "SLPA";
    public const string RoleWatershed = "WSHD";

    public static void AttachSlopeArrow(Entity entity, double slopePercent, string? surfaceName = null)
    {
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleSlopeArrow),
            new TypedValue((int)DxfCode.ExtendedDataReal, slopePercent));
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
        }

        Set(entity, buffer);
    }

    public static void AttachWatershed(Entity entity, int basinId, string? surfaceName = null)
    {
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleWatershed),
            new TypedValue((int)DxfCode.ExtendedDataInteger32, basinId));
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
        }

        Set(entity, buffer);
    }

    public static bool TryGetRole(Entity entity, out string role) =>
        TryGetRole(entity, out role, out _);

    public static bool TryGetRole(Entity entity, out string role, out string? surfaceName)
    {
        role = string.Empty;
        surfaceName = null;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].TypeCode != (int)DxfCode.ExtendedDataAsciiString)
            {
                continue;
            }

            var text = Convert.ToString(items[i].Value) ?? string.Empty;
            if (text is not (RoleSlopeArrow or RoleWatershed))
            {
                continue;
            }

            role = text;
            for (var j = i + 1; j < items.Length; j++)
            {
                if (items[j].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                {
                    surfaceName = Convert.ToString(items[j].Value);
                    break;
                }
            }

            return true;
        }

        return false;
    }

    private static void Set(Entity entity, ResultBuffer buffer)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        entity.XData = buffer;
    }
}
