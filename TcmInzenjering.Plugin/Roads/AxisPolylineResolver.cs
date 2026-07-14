using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisPolylineResolver
{
    public static bool TryResolve(Database db, long handleValue, out ObjectId polylineId)
    {
        polylineId = ObjectId.Null;
        if (handleValue == 0)
        {
            return false;
        }

        try
        {
            polylineId = db.GetObjectId(false, new Handle(handleValue), 0);
            return !polylineId.IsNull && polylineId.IsValid && !polylineId.IsErased;
        }
        catch
        {
            return false;
        }
    }
}
