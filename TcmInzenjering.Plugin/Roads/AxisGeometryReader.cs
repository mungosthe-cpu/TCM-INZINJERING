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
            Arc arc => ReadArc(arc),
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

    private static AlignmentElement ReadArc(Arc arc)
    {
        var start = arc.StartPoint;
        var end = arc.EndPoint;
        var center = arc.Center;
        var clockwise = IsClockwiseArc(arc);

        return new AlignmentElement
        {
            Type = AlignmentElementType.Arc,
            Start = start,
            End = end,
            Length = arc.Length,
            Radius = arc.Radius,
            Center = center,
            Clockwise = clockwise
        };
    }

    private static bool IsClockwiseArc(Arc arc)
    {
        var start = arc.StartPoint;
        var end = arc.EndPoint;
        var chord = end - start;
        if (chord.Length < 1e-9)
        {
            return false;
        }

        var chordDir = chord.GetNormal();
        var centerVec = arc.Center - start;
        var cross = chordDir.X * centerVec.Y - chordDir.Y * centerVec.X;
        return cross < 0;
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
