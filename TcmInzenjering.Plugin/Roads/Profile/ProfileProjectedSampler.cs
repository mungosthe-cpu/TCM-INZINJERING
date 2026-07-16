using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Učitava (stacionaža, Z) sa projektovane 3D nivelete (TCM_OSOVINA_3D / RoleProjectedAxis).
/// </summary>
internal static class ProfileProjectedSampler
{
    public static bool TryLoadSamples(
        Transaction tr,
        Database db,
        string axisName,
        out List<(double Station, double Elevation)> samples)
    {
        samples = new List<(double, double)>();
        Polyline3d? projected = null;

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        RoadDrawing.RunWithUnlockedProjectedAxisLayer(tr, db, () =>
        {
            foreach (ObjectId id in modelSpace)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Polyline3d poly || poly.IsErased)
                {
                    continue;
                }

                if (!RoadXData.TryReadProjectedAxis(poly, out var name))
                {
                    continue;
                }

                if (!string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                projected = poly;
                break;
            }
        });

        if (projected is null)
        {
            return false;
        }

        var vertices = new List<Point3d>();
        foreach (ObjectId vid in projected)
        {
            if (tr.GetObject(vid, OpenMode.ForRead) is PolylineVertex3d v)
            {
                vertices.Add(v.Position);
            }
        }

        if (vertices.Count < 2)
        {
            return false;
        }

        var metadata = RoadAxisStore.Load(tr, db, axisName);
        var startStation = metadata?.StartStation ?? 0.0;
        var station = startStation;
        samples.Add((station, vertices[0].Z));

        for (var i = 1; i < vertices.Count; i++)
        {
            var prev = vertices[i - 1];
            var curr = vertices[i];
            var dx = curr.X - prev.X;
            var dy = curr.Y - prev.Y;
            station += Math.Sqrt(dx * dx + dy * dy);
            samples.Add((station, curr.Z));
        }

        return samples.Count >= 2;
    }
}
