using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisReferenceTracker
{
    private static readonly Dictionary<string, Point3d> ReferencePoints =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Update(string axisName, Point3d referencePoint)
    {
        ReferencePoints[axisName] = referencePoint;
    }

    public static bool TryGet(string axisName, out Point3d referencePoint) =>
        ReferencePoints.TryGetValue(axisName, out referencePoint);

    public static void Remove(string axisName) => ReferencePoints.Remove(axisName);
}
