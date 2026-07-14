using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisGeometryUpdater
{
    private const double ChangeTolerance = 1e-4;

    public static bool Reconcile(Transaction tr, Database db, string axisName, double designRadius, double startStation)
    {
        var entries = LoadAxisEntities(tr, db, axisName);
        if (entries.Count < 3)
        {
            return false;
        }

        var changed = false;
        for (var i = 1; i < entries.Count - 1; i++)
        {
            if (entries[i].Element.Type != AlignmentElementType.Arc)
            {
                continue;
            }

            if (TryReconcileCorner(entries[i - 1], entries[i], entries[i + 1], designRadius))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        var elements = entries.Select(entry => entry.Element).ToList();
        ArcOrientation.OrientArcsToChainage(elements);
        AssignStations(elements, startStation);

        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Element = elements[i];
            WriteElement(tr, entries[i], axisName, i);
        }

        return true;
    }

    private static bool TryReconcileCorner(
        AxisEntityEntry prevEntry,
        AxisEntityEntry arcEntry,
        AxisEntityEntry nextEntry,
        double designRadius)
    {
        if (prevEntry.Entity is not Line prevLine || nextEntry.Entity is not Line nextLine)
        {
            return false;
        }

        var p0 = TangentArcGeometry.To2d(prevLine.StartPoint);
        var p1 = TangentArcGeometry.To2d(prevLine.EndPoint);
        var p2 = TangentArcGeometry.To2d(nextLine.StartPoint);
        var p3 = TangentArcGeometry.To2d(nextLine.EndPoint);

        if (!TangentArcGeometry.TryIntersectLines(p0, p1, p2, p3, out var pi))
        {
            return false;
        }

        var inDir = new Vector2d(p1.X - p0.X, p1.Y - p0.Y);
        var outDir = new Vector2d(p3.X - p2.X, p3.Y - p2.Y);
        var incomingLength = Math.Abs(new Vector2d(p0.X - pi.X, p0.Y - pi.Y).DotProduct(inDir.GetNormal()));
        var outgoingLength = Math.Abs(new Vector2d(p3.X - pi.X, p3.Y - pi.Y).DotProduct(outDir.GetNormal()));
        if (!TangentArcGeometry.TryBuildCornerArc(
                pi,
                inDir,
                outDir,
                designRadius,
                out var arcStart,
                out var arcEnd,
                out var radius,
                out var clockwise,
                incomingLength * 0.98,
                outgoingLength * 0.98))
        {
            return false;
        }

        var newArc = TangentArcGeometry.CreateArcElement(arcStart, arcEnd, radius, clockwise);
        if (!HasGeometryChanged(prevLine, nextLine, arcEntry.Element, newArc, arcStart, arcEnd))
        {
            return false;
        }

        prevLine.StartPoint = TangentArcGeometry.To3d(p0);
        prevLine.EndPoint = newArc.Start;
        nextLine.StartPoint = newArc.End;
        nextLine.EndPoint = TangentArcGeometry.To3d(p3);

        prevEntry.Element = CreateTangentElement(p0, arcStart);
        nextEntry.Element = CreateTangentElement(arcEnd, p3);
        arcEntry.Element = newArc;
        UpdateArcEntity((Arc)arcEntry.Entity, newArc);
        return true;
    }

    private static AlignmentElement CreateTangentElement(Point2d start, Point2d end)
    {
        var start3d = TangentArcGeometry.To3d(start);
        var end3d = TangentArcGeometry.To3d(end);
        return new AlignmentElement
        {
            Type = AlignmentElementType.Tangent,
            Start = start3d,
            End = end3d,
            Length = start3d.DistanceTo(end3d),
            Radius = 0,
            Center = Point3d.Origin,
            Clockwise = false
        };
    }

    private static bool HasGeometryChanged(
        Line prevLine,
        Line nextLine,
        AlignmentElement currentArc,
        AlignmentElement newArc,
        Point2d arcStart,
        Point2d arcEnd)
    {
        if (TangentArcGeometry.To2d(prevLine.EndPoint).GetDistanceTo(arcStart) > ChangeTolerance)
        {
            return true;
        }

        if (TangentArcGeometry.To2d(nextLine.StartPoint).GetDistanceTo(arcEnd) > ChangeTolerance)
        {
            return true;
        }

        if (Math.Abs(currentArc.Radius - newArc.Radius) > ChangeTolerance)
        {
            return true;
        }

        if (currentArc.Start.DistanceTo(newArc.Start) > ChangeTolerance ||
            currentArc.End.DistanceTo(newArc.End) > ChangeTolerance ||
            currentArc.Center.DistanceTo(newArc.Center) > ChangeTolerance)
        {
            return true;
        }

        return false;
    }

    private static void UpdateArcEntity(Arc arc, AlignmentElement element)
    {
        if (!arc.IsWriteEnabled)
        {
            arc.UpgradeOpen();
        }

        var startVec = element.Start - element.Center;
        var endVec = element.End - element.Center;
        var startAngle = Math.Atan2(startVec.Y, startVec.X);
        var endAngle = Math.Atan2(endVec.Y, endVec.X);

        arc.Center = element.Center;
        arc.Radius = element.Radius;
        if (element.Clockwise)
        {
            arc.StartAngle = endAngle;
            arc.EndAngle = startAngle;
        }
        else
        {
            arc.StartAngle = startAngle;
            arc.EndAngle = endAngle;
        }
    }

    private static void WriteElement(Transaction tr, AxisEntityEntry entry, string axisName, int index)
    {
        if (entry.Entity is Line line)
        {
            if (!line.IsWriteEnabled)
            {
                line.UpgradeOpen();
            }

            line.StartPoint = entry.Element.Start;
            line.EndPoint = entry.Element.End;
        }

        RoadXData.AttachAxisElement(entry.Entity, axisName, index, entry.Element);
    }

    private static List<AxisEntityEntry> LoadAxisEntities(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        var entries = new List<AxisEntityEntry>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased)
            {
                continue;
            }

            var entity = tr.GetObject(id, OpenMode.ForWrite) as Entity;
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
                entries.Add(new AxisEntityEntry(index, entity, element));
            }
        }

        return entries
            .OrderBy(entry => entry.Index)
            .ToList();
    }

    private static AlignmentElement? ReadElement(Entity entity)
    {
        return entity switch
        {
            Line line => new AlignmentElement
            {
                Type = AlignmentElementType.Tangent,
                Start = line.StartPoint,
                End = line.EndPoint,
                Length = line.Length,
                Radius = 0,
                Center = Point3d.Origin,
                Clockwise = false
            },
            Arc arc => ArcOrientation.ReadArc(arc),
            _ => null
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

    private sealed class AxisEntityEntry(int index, Entity entity, AlignmentElement element)
    {
        public int Index { get; } = index;
        public Entity Entity { get; } = entity;
        public AlignmentElement Element { get; set; } = element;
    }
}
