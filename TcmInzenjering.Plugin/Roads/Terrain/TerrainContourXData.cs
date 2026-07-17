using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData za izohipse, kotne oznake i spot elevaciju (+ opciono ime terena).</summary>
internal static class TerrainContourXData
{
    public const string RoleContour = "IZO";
    public const string RoleContourLabel = "IZOL";
    public const string RoleContourWipe = "IZOW";
    public const string RoleSpot = "SPOT";

    public static void AttachContour(Entity entity, double elevation, bool isMajor, string? surfaceName = null)
    {
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleContour),
            new TypedValue((int)DxfCode.ExtendedDataReal, elevation),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)(isMajor ? 1 : 0)));
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
        }

        Set(entity, buffer);
    }

    public static void AttachContourLabel(Entity entity, double elevation) =>
        AttachContourLabel(entity, elevation, parentHandle: 0, distanceAlong: 0, surfaceName: null, wipeoutHandle: 0);

    public static void AttachContourLabel(
        Entity entity,
        double elevation,
        long parentHandle,
        double distanceAlong,
        string? surfaceName = null,
        long wipeoutHandle = 0)
    {
        // Parent / wipeout kao hex string — ne ExtendedDataHandle (nestabilno).
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleContourLabel),
            new TypedValue((int)DxfCode.ExtendedDataReal, elevation),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, parentHandle.ToString("X")),
            new TypedValue((int)DxfCode.ExtendedDataReal, distanceAlong),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName?.Trim() ?? ""),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, wipeoutHandle > 0 ? wipeoutHandle.ToString("X") : ""));
        Set(entity, buffer);
    }

    public static void AttachContourWipeout(
        Entity wipeout,
        long labelHandle,
        double elevation,
        string? surfaceName = null)
    {
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleContourWipe),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, labelHandle.ToString("X")),
            new TypedValue((int)DxfCode.ExtendedDataReal, elevation));
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
        }

        Set(wipeout, buffer);
    }

    public static void AttachSpot(Entity entity, double elevation, string? surfaceName = null)
    {
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleSpot),
            new TypedValue((int)DxfCode.ExtendedDataReal, elevation));
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
        }

        Set(entity, buffer);
    }

    public static bool TryReadRole(Entity entity, out string role, out double elevation)
    {
        role = string.Empty;
        elevation = 0;
        return TryReadContourLabel(entity, out role, out elevation, out _, out _, out _, out _)
               || TryReadSimpleRole(entity, out role, out elevation, out _);
    }

    public static bool TryGetSurfaceName(Entity entity, out string? surfaceName)
    {
        surfaceName = null;
        if (TryReadContourLabel(entity, out _, out _, out _, out _, out surfaceName, out _) &&
            !string.IsNullOrWhiteSpace(surfaceName))
        {
            return true;
        }

        return TryReadSimpleRole(entity, out _, out _, out surfaceName) &&
               !string.IsNullOrWhiteSpace(surfaceName);
    }

    public static bool TryReadContourLabel(
        Entity entity,
        out string role,
        out double elevation,
        out long parentHandle,
        out double distanceAlong) =>
        TryReadContourLabel(entity, out role, out elevation, out parentHandle, out distanceAlong, out _, out _);

    public static bool TryReadContourLabel(
        Entity entity,
        out string role,
        out double elevation,
        out long parentHandle,
        out double distanceAlong,
        out string? surfaceName) =>
        TryReadContourLabel(entity, out role, out elevation, out parentHandle, out distanceAlong, out surfaceName, out _);

    public static bool TryReadContourLabel(
        Entity entity,
        out string role,
        out double elevation,
        out long parentHandle,
        out double distanceAlong,
        out string? surfaceName,
        out long wipeoutHandle)
    {
        role = string.Empty;
        elevation = 0;
        parentHandle = 0;
        distanceAlong = 0;
        surfaceName = null;
        wipeoutHandle = 0;
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
            if (text != RoleContourLabel)
            {
                continue;
            }

            role = text;
            if (i + 1 < items.Length && items[i + 1].TypeCode == (int)DxfCode.ExtendedDataReal)
            {
                elevation = Convert.ToDouble(items[i + 1].Value);
            }

            parentHandle = ReadHexHandle(items, i + 2);
            if (i + 3 < items.Length && items[i + 3].TypeCode == (int)DxfCode.ExtendedDataReal)
            {
                distanceAlong = Convert.ToDouble(items[i + 3].Value);
            }

            if (i + 4 < items.Length && items[i + 4].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
            {
                var s = Convert.ToString(items[i + 4].Value);
                surfaceName = string.IsNullOrWhiteSpace(s) ? null : s;
            }

            wipeoutHandle = ReadHexHandle(items, i + 5);
            return true;
        }

        return false;
    }

    private static long ReadHexHandle(TypedValue[] items, int index)
    {
        if (index < 0 || index >= items.Length)
        {
            return 0;
        }

        if (items[index].TypeCode == (int)DxfCode.ExtendedDataHandle)
        {
            return ((Handle)items[index].Value!).Value;
        }

        if (items[index].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
        {
            var hex = Convert.ToString(items[index].Value);
            if (!string.IsNullOrWhiteSpace(hex) &&
                long.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var h))
            {
                return h;
            }
        }

        return 0;
    }

    private static bool TryReadSimpleRole(
        Entity entity,
        out string role,
        out double elevation,
        out string? surfaceName)
    {
        role = string.Empty;
        elevation = 0;
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
            if (text is not (RoleContour or RoleSpot))
            {
                continue;
            }

            role = text;
            var next = i + 1;
            if (next < items.Length && items[next].TypeCode == (int)DxfCode.ExtendedDataReal)
            {
                elevation = Convert.ToDouble(items[next].Value);
                next++;
            }

            // Contour: Real, Integer16, optional surface string
            if (role == RoleContour &&
                next < items.Length &&
                items[next].TypeCode == (int)DxfCode.ExtendedDataInteger16)
            {
                next++;
            }

            if (next < items.Length && items[next].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
            {
                surfaceName = Convert.ToString(items[next].Value);
            }

            return true;
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
