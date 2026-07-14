using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal static class CrossAxisGeometry
{
    public static bool TryGetFrame(Entity entity, out Point3d origin, out Vector3d along, out Vector3d across)
    {
        origin = Point3d.Origin;
        along = Vector3d.XAxis;
        across = Vector3d.YAxis;

        switch (entity)
        {
            case Line line:
                origin = line.StartPoint + (line.EndPoint - line.StartPoint) * 0.5;
                along = GetPlanDirection(line.StartPoint, line.EndPoint);
                break;
            case Polyline polyline when polyline.NumberOfVertices >= 2:
            {
                var start = polyline.GetPoint3dAt(0);
                var end = polyline.GetPoint3dAt(polyline.NumberOfVertices - 1);
                try
                {
                    origin = polyline.GetPointAtDist(polyline.Length * 0.5);
                }
                catch
                {
                    origin = start + (end - start) * 0.5;
                }

                along = GetPlanDirection(start, end);
                break;
            }
            default:
                return false;
        }

        if (along.Length < 1e-9)
        {
            return false;
        }

        along = along.GetNormal();
        across = new Vector3d(-along.Y, along.X, 0).GetNormal();
        return true;
    }

    public static Point3d ComputePlacement(
        Point3d origin,
        Vector3d along,
        Vector3d across,
        CrossAxisOffsetSettings settings)
    {
        var side = settings.Side == CrossAxisSide.Right ? 1.0 : -1.0;
        return origin
            + along * settings.OffsetX
            + across * (settings.OffsetY * side);
    }

    private static Vector3d GetPlanDirection(Point3d start, Point3d end)
    {
        var delta = end - start;
        return new Vector3d(delta.X, delta.Y, 0);
    }
}
