using Autodesk.AutoCAD.DatabaseServices;

using Autodesk.AutoCAD.Geometry;



namespace TcmInzenjering.Plugin.Roads.CrossAxis;



internal static class CrossAxisXData

{

    public const string RoleCrossAxis = "CAXIS";

    public const string RoleCrossLabel = "CXLB";

    public const string RoleCrossStation = "CXST";



    public static void AttachCrossAxis(Entity entity, int number, string? parentRoadAxisName = null)

    {

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

        if (items.Length < 3 || items[1].Value?.ToString() != RoleCrossAxis)

        {

            return false;

        }



        number = Convert.ToInt32(items[2].Value);

        if (items.Length >= 4)

        {

            parentRoadAxisName = items[3].Value?.ToString() ?? string.Empty;

        }



        return number > 0;

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


