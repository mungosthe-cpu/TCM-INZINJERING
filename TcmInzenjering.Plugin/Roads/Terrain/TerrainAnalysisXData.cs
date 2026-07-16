using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData za slope strelice i watershed polilinije.</summary>
internal static class TerrainAnalysisXData
{
    public const string RoleSlopeArrow = "SLPA";
    public const string RoleWatershed = "WSHD";

    public static void AttachSlopeArrow(Entity entity, double slopePercent)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleSlopeArrow),
            new TypedValue((int)DxfCode.ExtendedDataReal, slopePercent)));
    }

    public static void AttachWatershed(Entity entity, int basinId)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleWatershed),
            new TypedValue((int)DxfCode.ExtendedDataInteger32, basinId)));
    }

    public static bool TryGetRole(Entity entity, out string role)
    {
        role = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        foreach (var item in values.AsArray())
        {
            if (item.TypeCode != (int)DxfCode.ExtendedDataAsciiString)
            {
                continue;
            }

            var text = Convert.ToString(item.Value) ?? string.Empty;
            if (text is RoleSlopeArrow or RoleWatershed)
            {
                role = text;
                return true;
            }
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
