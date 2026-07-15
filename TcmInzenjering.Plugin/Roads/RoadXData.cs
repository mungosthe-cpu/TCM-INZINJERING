using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

internal static class RoadXData
{
    public const string RoleAxis = "AXIS";
    public const string RoleTick = "TICK";
    public const string RoleText = "TEXT";
    /// <summary>Deo stacionaze (npr. 0-380.00) kod projektnog formata — odvojeno od RoleText (OSA 20).</summary>
    public const string RoleChainage = "CHNG";
    public const string RoleRadiusText = "RTXT";
    public const string RoleRadiusDimArc = "RARC";
    public const string RoleRadiusArrowStart = "RARS";
    public const string RoleRadiusArrowEnd = "RARE";
    public const string RoleRadiusTick = "RTCK";
    public const string RoleSourcePolyline = "SRCPL";
    public const string RoleSegmentText = "SEGT";
    public const string RoleTable = "TABL";
    /// <summary>Čvor tangentnog poligona (T1, T2…) — marker, oznaka, tabela, leader.</summary>
    public const string RoleTangentNode = "TNODE";
    /// <summary>3D polilinija — projekcija ose na teren.</summary>
    public const string RoleProjectedAxis = "PROJ";

    public static void AttachSourcePolyline(Entity entity, string axisName)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleSourcePolyline),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName)));
    }

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

    public static void AttachSegmentLabel(Entity entity, string axisName, int index)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleSegmentText),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)index)));
    }

    public static void AttachAxisTable(Entity entity, string axisName)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTable),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName)));
    }

    public static void AttachTangentNode(Entity entity, string axisName, int nodeNumber)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTangentNode),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)nodeNumber)));
    }

    public static void AttachProjectedAxis(Entity entity, string axisName)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleProjectedAxis),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName)));
    }

    public static bool TryReadProjectedAxis(Entity entity, out string axisName)
    {
        axisName = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 3 || items[1].Value?.ToString() != RoleProjectedAxis)
        {
            return false;
        }

        axisName = items[2].Value?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(axisName);
    }

    public static bool TryReadSourcePolyline(Entity entity, out string axisName)
    {
        axisName = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 3 || items[1].Value?.ToString() != RoleSourcePolyline)
        {
            return false;
        }

        axisName = items[2].Value?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(axisName);
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
        if (role is not RoleTick and not RoleText and not RoleChainage)
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

    public static bool TryReadSegmentLabel(Entity entity, out string axisName)
    {
        axisName = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 3 || items[1].Value?.ToString() != RoleSegmentText)
        {
            return false;
        }

        axisName = items[2].Value?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(axisName);
    }

    public static bool TryReadTangentNode(Entity entity, out string axisName, out int nodeNumber)
    {
        axisName = string.Empty;
        nodeNumber = 0;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 4 || items[1].Value?.ToString() != RoleTangentNode)
        {
            return false;
        }

        axisName = items[2].Value?.ToString() ?? string.Empty;
        nodeNumber = Convert.ToInt32(items[3].Value);
        return !string.IsNullOrWhiteSpace(axisName) && nodeNumber > 0;
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
