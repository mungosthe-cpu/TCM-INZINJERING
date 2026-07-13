using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

internal static class RoadXData
{
    public const string RoleAxis = "AXIS";
    public const string RoleTick = "TICK";
    public const string RoleText = "TEXT";
    public const string RoleRadiusText = "RTXT";
    public const string RoleRadiusDimArc = "RARC";
    public const string RoleRadiusArrowStart = "RARS";
    public const string RoleRadiusArrowEnd = "RARE";
    public const string RoleRadiusTick = "RTCK";

    public static void AttachAxisElement(Entity entity, string axisName, int index, AlignmentElement element)
    {
        var typeCode = element.Type == AlignmentElementType.Tangent ? "T" : "A";
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleAxis),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)index),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, typeCode),
            new TypedValue((int)DxfCode.ExtendedDataReal, element.StartStation),
            new TypedValue((int)DxfCode.ExtendedDataReal, element.EndStation),
            new TypedValue((int)DxfCode.ExtendedDataReal, element.Radius)));
    }

    public static void AttachStationLabel(Entity entity, string axisName, string role, double station)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, role),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName),
            new TypedValue((int)DxfCode.ExtendedDataReal, station)));
    }

    public static void AttachRadiusAnnotation(Entity entity, string axisName, string role, int arcIndex, double radius)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, role),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)arcIndex),
            new TypedValue((int)DxfCode.ExtendedDataReal, radius)));
    }

    public static bool TryReadAxisElement(Entity entity, out string axisName, out int index)
    {
        axisName = string.Empty;
        index = -1;

        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 4 || items[1].Value?.ToString() != RoleAxis)
        {
            return false;
        }

        axisName = items[2].Value?.ToString() ?? string.Empty;
        index = Convert.ToInt32(items[3].Value);
        return !string.IsNullOrWhiteSpace(axisName) && index >= 0;
    }

    public static bool TryReadStationLabel(Entity entity, out string axisName, out string role, out double station)
    {
        axisName = string.Empty;
        role = string.Empty;
        station = 0;

        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 4)
        {
            return false;
        }

        role = items[1].Value?.ToString() ?? string.Empty;
        if (role is not RoleTick and not RoleText)
        {
            return false;
        }

        axisName = items[2].Value?.ToString() ?? string.Empty;
        station = Convert.ToDouble(items[3].Value);
        return !string.IsNullOrWhiteSpace(axisName);
    }

    public static bool TryReadRadiusAnnotation(Entity entity, out string axisName, out string role)
    {
        axisName = string.Empty;
        role = string.Empty;

        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 4)
        {
            return false;
        }

        role = items[1].Value?.ToString() ?? string.Empty;
        if (role is not RoleRadiusText and not RoleRadiusDimArc and not RoleRadiusArrowStart and not RoleRadiusArrowEnd and not RoleRadiusTick)
        {
            return false;
        }

        axisName = items[2].Value?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(axisName);
    }

    public static bool IsManagedEntity(Entity entity)
    {
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        return values is not null;
    }

    private static void SetXData(Entity entity, ResultBuffer buffer)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        entity.XData = buffer;
    }
}
