using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisGeometryReader
{
    public static RoadAxis? ReadAxis(Transaction tr, Database db, string axisName, double startStation)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        var entries = new List<(int Index, AlignmentElement Element)>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased)
            {
                continue;
            }

            var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (entity is null || entity.Layer != RoadDrawing.AxisLayerName ||
                !RoadXData.TryReadAxisElement(entity, out var name, out var index))
            {
                continue;
            }

            if (!string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var element = ReadElement(entity);
            if (element is not null)
            {
                entries.Add((index, element));
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var elements = entries
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.Element)
            .ToList();

        ArcOrientation.OrientArcsToChainage(elements);
        AssignStations(elements, startStation);

        return new RoadAxis
        {
            Name = axisName,
            StartStation = startStation,
            Elements = elements
        };
    }

    private static AlignmentElement? ReadElement(Entity entity)
    {
        return entity switch
        {
            Line line => ReadLine(line),
            Arc arc => ArcOrientation.ReadArc(arc),
            Polyline pl => ReadSpiral(pl),
            _ => null
        };
    }

    private static AlignmentElement ReadLine(Line line)
    {
        return new AlignmentElement
        {
            Type = AlignmentElementType.Tangent,
            Start = line.StartPoint,
            End = line.EndPoint,
            Length = line.Length,
            Radius = 0,
            Center = Point3d.Origin,
            Clockwise = false
        };
    }

    private static AlignmentElement ReadSpiral(Polyline pl)
    {
        var pts = new List<Point3d>(pl.NumberOfVertices);
        for (var i = 0; i < pl.NumberOfVertices; i++)
        {
            pts.Add(pl.GetPoint3dAt(i));
        }

        var length = 0.0;
        for (var i = 1; i < pts.Count; i++)
        {
            length += pts[i - 1].DistanceTo(pts[i]);
        }

        // Radius / A iz XData ako postoji.
        double radius = 0;
        double spiralA = 0;
        var xd = pl.GetXDataForApplication(RoadDrawing.RegAppName);
        if (xd is not null)
        {
            var items = xd.AsArray();
            if (items.Length >= 8)
            {
                radius = Convert.ToDouble(items[7].Value);
            }

            if (items.Length >= 9)
            {
                spiralA = Convert.ToDouble(items[8].Value);
            }

            if (items.Length >= 10)
            {
                length = Convert.ToDouble(items[9].Value);
            }
        }

        return new AlignmentElement
        {
            Type = AlignmentElementType.Spiral,
            Start = pts.Count > 0 ? pts[0] : Point3d.Origin,
            End = pts.Count > 0 ? pts[^1] : Point3d.Origin,
            Length = Math.Max(length, 1e-6),
            Radius = radius,
            Center = Point3d.Origin,
            Clockwise = false,
            SpiralPoints = pts,
            SpiralA = spiralA > 1e-9
                ? spiralA
                : Math.Sqrt(Math.Max(1e-12, radius * length))
        };
    }

    private static void AssignStations(List<AlignmentElement> elements, double startStation)
    {
        var station = startStation;
        foreach (var element in elements)
        {
            element.StartStation = station;
            station += element.Length;
            element.EndStation = station;
        }
    }
}
