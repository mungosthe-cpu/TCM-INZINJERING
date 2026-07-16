using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData za izohipse, kotne oznake i spot elevaciju.</summary>
internal static class TerrainContourXData
{
    public const string RoleContour = "IZO";
    public const string RoleContourLabel = "IZOL";
    public const string RoleSpot = "SPOT";

    public static void AttachContour(Entity entity, double elevation, bool isMajor)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleContour),
            new TypedValue((int)DxfCode.ExtendedDataReal, elevation),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)(isMajor ? 1 : 0))));
    }

    public static void AttachContourLabel(Entity entity, double elevation)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleContourLabel),
            new TypedValue((int)DxfCode.ExtendedDataReal, elevation)));
    }

    public static void AttachSpot(Entity entity, double elevation)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleSpot),
            new TypedValue((int)DxfCode.ExtendedDataReal, elevation)));
    }

    public static bool TryReadRole(Entity entity, out string role, out double elevation)
    {
        role = string.Empty;
        elevation = 0;
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
            if (text is RoleContour or RoleContourLabel or RoleSpot)
            {
                role = text;
                if (i + 1 < items.Length && items[i + 1].TypeCode == (int)DxfCode.ExtendedDataReal)
                {
                    elevation = Convert.ToDouble(items[i + 1].Value);
                }

                return true;
            }
        }

        return false;
    }

    public static bool IsContour(Entity entity) =>
        TryReadRole(entity, out var role, out _) && role == RoleContour;

    private static void Set(Entity entity, ResultBuffer buffer)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        entity.XData = buffer;
    }
}
