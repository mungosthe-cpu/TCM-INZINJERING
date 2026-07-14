using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class PolylineStationPicker
{
    public static bool TryPickDistance(
        Document doc,
        ObjectId polylineId,
        string prompt,
        out double distanceAlong)
    {
        distanceAlong = 0;
        if (doc is null || polylineId.IsNull)
        {
            return false;
        }

        using var docLock = doc.LockDocument();
        var ed = doc.Editor;
        var db = doc.Database;

        var pointOptions = new PromptPointOptions($"\n{prompt}")
        {
            AllowNone = false
        };

        var pointResult = ed.GetPoint(pointOptions);
        if (pointResult.Status != PromptStatus.OK)
        {
            return false;
        }

        using var tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(polylineId, OpenMode.ForRead) is not Polyline polyline)
        {
            return false;
        }

        var closest = polyline.GetClosestPointTo(pointResult.Value, false);
        distanceAlong = polyline.GetDistAtPoint(closest);
        if (double.IsNaN(distanceAlong) || double.IsInfinity(distanceAlong))
        {
            return false;
        }

        tr.Commit();
        return true;
    }
}
