using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal static class CrossAxisXData
{
    public const string RoleCrossAxis = "CAXIS";
    public const string RoleCrossLabel = "CXLB";
    public const string RoleCrossStation = "CXST";

    /// <summary>
    /// Veže CAXIS. Ako entitet već ima RoleTick stacionažu, zadržava je (spojeni XData).
    /// </summary>
    public static void AttachCrossAxis(Entity entity, int number, string? parentRoadAxisName = null)
    {
        if (RoadXData.TryReadStationLabel(entity, out var axisName, out var role, out var station) &&
            role == RoadXData.RoleTick)
        {
            AttachStationTickWithCrossAxis(
                entity,
                string.IsNullOrWhiteSpace(parentRoadAxisName) ? axisName : parentRoadAxisName!,
                station,
                number);
            return;
        }

        var values = new List<TypedValue>
        {
            new((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new((int)DxfCode.ExtendedDataAsciiString, RoleCrossAxis),
            new((int)DxfCode.ExtendedDataInteger32, number)
        };

        if (!string.IsNullOrWhiteSpace(parentRoadAxisName))
        {
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, parentRoadAxisName));
        }

        SetXData(entity, new ResultBuffer(values.ToArray()));
    }

    /// <summary>
    /// RoleTick + CAXIS u istom XData — oba čitača rade.
    /// Format: TICK, axisName, station, CAXIS, number
    /// </summary>
    public static void AttachStationTickWithCrossAxis(
        Entity entity,
        string axisName,
        double station,
        int number)
    {
        SetXData(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoadXData.RoleTick),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName),
            new TypedValue((int)DxfCode.ExtendedDataReal, station),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleCrossAxis),
            new TypedValue((int)DxfCode.ExtendedDataInteger32, number)));
    }

    public static void AttachCrossAnnotation(
        Entity entity,
        string role,
        int crossAxisNumber,
        string? parentRoadAxisName = null)
    {
        var values = new List<TypedValue>
        {
            new((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new((int)DxfCode.ExtendedDataAsciiString, role),
            new((int)DxfCode.ExtendedDataInteger32, crossAxisNumber)
        };

        if (!string.IsNullOrWhiteSpace(parentRoadAxisName))
        {
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, parentRoadAxisName));
        }

        SetXData(entity, new ResultBuffer(values.ToArray()));
    }

    public static bool TryReadCrossAxis(Entity entity, out int number) =>
        TryReadCrossAxis(entity, out number, out _);

    public static bool TryReadCrossAxis(Entity entity, out int number, out string parentRoadAxisName)
    {
        number = 0;
        parentRoadAxisName = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 3)
        {
            return false;
        }

        var role = items[1].Value?.ToString() ?? string.Empty;

        // Stari format: CAXIS, number, [parent]
        if (role == RoleCrossAxis)
        {
            number = Convert.ToInt32(items[2].Value);
            if (items.Length >= 4)
            {
                parentRoadAxisName = items[3].Value?.ToString() ?? string.Empty;
            }

            return number > 0;
        }

        // Spojeni format: TICK, axisName, station, CAXIS, number
        if (role == RoadXData.RoleTick &&
            items.Length >= 6 &&
            string.Equals(items[4].Value?.ToString(), RoleCrossAxis, StringComparison.Ordinal))
        {
            parentRoadAxisName = items[2].Value?.ToString() ?? string.Empty;
            number = Convert.ToInt32(items[5].Value);
            return number > 0;
        }

        return false;
    }

    public static bool TryReadCrossAnnotation(Entity entity, out string role, out int crossAxisNumber) =>
        TryReadCrossAnnotation(entity, out role, out crossAxisNumber, out _);

    public static bool TryReadCrossAnnotation(
        Entity entity,
        out string role,
        out int crossAxisNumber,
        out string parentRoadAxisName)
    {
        role = string.Empty;
        crossAxisNumber = 0;
        parentRoadAxisName = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 3)
        {
            return false;
        }

        role = items[1].Value?.ToString() ?? string.Empty;
        if (role is not RoleCrossLabel and not RoleCrossStation)
        {
            return false;
        }

        crossAxisNumber = Convert.ToInt32(items[2].Value);
        if (items.Length >= 4)
        {
            parentRoadAxisName = items[3].Value?.ToString() ?? string.Empty;
        }

        return crossAxisNumber > 0;
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
