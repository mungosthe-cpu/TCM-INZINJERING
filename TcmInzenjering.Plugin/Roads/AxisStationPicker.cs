using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisStationPicker
{
    public static bool TryPickStation(
        Document doc,
        RoadAxis axis,
        RoadAxisMetadata? metadata,
        string prompt,
        out double station)
    {
        station = 0;
        if (doc is null || axis.Elements.Count == 0)
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

        // Uvek projektuj na geometriju osovine (tangente/lukove), ne na izvornu poliliniju.
        if (TryFindStationAtPoint(axis, pointResult.Value, out station))
        {
            return true;
        }

        using var tr = db.TransactionManager.StartTransaction();
        if (metadata?.HasSourcePolyline == true &&
            AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out var polylineId) &&
            tr.GetObject(polylineId, OpenMode.ForRead) is Polyline polyline)
        {
            var closest = polyline.GetClosestPointTo(pointResult.Value, false);
            var distanceAlong = polyline.GetDistAtPoint(closest);
            if (!double.IsNaN(distanceAlong) && !double.IsInfinity(distanceAlong))
            {
                station = AxisStationMapper.MapPolylineDistanceToAxisStation(polyline, axis, distanceAlong);
                tr.Commit();
                return true;
            }
        }

        tr.Commit();
        return false;
    }

    /// <summary>
    /// Više tačaka u crtežu; Enter završava izbor.
    /// </summary>
    public static int TryPickMultipleStations(
        Document doc,
        RoadAxis axis,
        out List<double> stations)
    {
        stations = new List<double>();
        if (doc is null || axis.Elements.Count == 0)
        {
            return 0;
        }

        using var docLock = doc.LockDocument();
        var ed = doc.Editor;

        while (true)
        {
            var pointOptions = new PromptPointOptions(
                "\nIzaberite položaj poprečne ose (Enter za povratak u prozor): ")
            {
                AllowNone = true
            };

            var pointResult = ed.GetPoint(pointOptions);
            if (pointResult.Status == PromptStatus.None)
            {
                break;
            }

            if (pointResult.Status == PromptStatus.Cancel)
            {
                stations.Clear();
                return 0;
            }

            if (pointResult.Status != PromptStatus.OK)
            {
                continue;
            }

            if (TryFindStationAtPoint(axis, pointResult.Value, out var station))
            {
                station = Math.Max(axis.StartStation, Math.Min(station, axis.Elements[^1].EndStation));
                stations.Add(station);
            }
        }

        return stations.Count;
    }

    public static bool TryFindStationAtPoint(RoadAxis axis, Point3d point, out double station)
    {
        station = axis.StartStation;
        var bestDistance = double.MaxValue;
        var found = false;

        foreach (var element in axis.Elements)
        {
            if (!TryClosestOnElement(element, point, out var elementStation, out var distance))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                station = elementStation;
                found = true;
            }
        }

        return found;
    }

    private static bool TryClosestOnElement(
        AlignmentElement element,
        Point3d point,
        out double station,
        out double distance)
    {
        station = element.StartStation;
        distance = double.MaxValue;

        if (element.Type == AlignmentElementType.Tangent)
        {
            var seg = new LineSegment3d(element.Start, element.End);
            var closest = seg.GetClosestPointTo(point);
            var along = element.Start.DistanceTo(closest.Point);
            var ratio = element.Length > 1e-9 ? along / element.Length : 0;
            station = element.StartStation + ratio * element.Length;
            distance = point.DistanceTo(closest.Point);
            return true;
        }

        if (element.Type == AlignmentElementType.Arc && element.Radius > 1e-9)
        {
            const int arcSamples = 64;
            var arcFound = false;
            for (var i = 0; i <= arcSamples; i++)
            {
                var t = i / (double)arcSamples;
                var st = element.StartStation + t * element.Length;
                var sample = SamplePointOnElement(element, t);
                if (sample is null)
                {
                    continue;
                }

                var dist = point.DistanceTo(sample.Value);
                if (dist < distance)
                {
                    distance = dist;
                    station = st;
                    arcFound = true;
                }
            }

            return arcFound;
        }

        const int samples = 48;
        var found = false;
        for (var i = 0; i <= samples; i++)
        {
            var t = i / (double)samples;
            var st = element.StartStation + t * element.Length;
            var sample = SamplePointOnElement(element, t);
            if (sample is null)
            {
                continue;
            }

            var dist = point.DistanceTo(sample.Value);
            if (dist < distance)
            {
                distance = dist;
                station = st;
                found = true;
            }
        }

        return found;
    }

    private static double StationAlongArc(AlignmentElement element, Point3d pointOnArc)
    {
        var startVec = element.Start - element.Center;
        var endVec = element.End - element.Center;
        var pointVec = pointOnArc - element.Center;
        var startAngle = Math.Atan2(startVec.Y, startVec.X);
        var endAngle = Math.Atan2(endVec.Y, endVec.X);
        var pointAngle = Math.Atan2(pointVec.Y, pointVec.X);
        var ratio = SweepRatio(startAngle, pointAngle, element.Clockwise, startAngle, endAngle, element.Clockwise);
        return element.StartStation + ratio * element.Length;
    }

    private static double SweepRatio(
        double arcStartAngle,
        double targetAngle,
        bool clockwise,
        double startAngle,
        double endAngle,
        bool arcClockwise)
    {
        arcStartAngle = Normalize(arcStartAngle);
        targetAngle = Normalize(targetAngle);
        if (clockwise)
        {
            if (targetAngle > arcStartAngle)
            {
                targetAngle -= Math.PI * 2;
            }
        }
        else if (targetAngle < arcStartAngle)
        {
            targetAngle += Math.PI * 2;
        }

        startAngle = Normalize(startAngle);
        endAngle = Normalize(endAngle);
        if (arcClockwise)
        {
            if (endAngle > startAngle)
            {
                endAngle -= Math.PI * 2;
            }

            var total = startAngle - endAngle;
            var swept = startAngle - targetAngle;
            return total < 1e-9 ? 0 : Math.Max(0, Math.Min(1, swept / total));
        }

        if (endAngle < startAngle)
        {
            endAngle += Math.PI * 2;
        }

        var totalCcw = endAngle - startAngle;
        var sweptCcw = targetAngle - startAngle;
        return totalCcw < 1e-9 ? 0 : Math.Max(0, Math.Min(1, sweptCcw / totalCcw));
    }

    private static Point3d? SamplePointOnElement(AlignmentElement element, double ratio)
    {
        ratio = Math.Max(0, Math.Min(1, ratio));
        if (element.Type == AlignmentElementType.Tangent)
        {
            var dir = element.End - element.Start;
            return element.Start + dir * ratio;
        }

        if (element.Type == AlignmentElementType.Spiral &&
            element.SpiralPoints is { Count: >= 2 } pts)
        {
            var index = ratio * (pts.Count - 1);
            var i0 = (int)Math.Floor(index);
            var i1 = Math.Min(i0 + 1, pts.Count - 1);
            var local = index - i0;
            return pts[i0] + (pts[i1] - pts[i0]) * local;
        }

        if (element.Type == AlignmentElementType.Arc && element.Radius > 1e-9)
        {
            var startVec = element.Start - element.Center;
            var endVec = element.End - element.Center;
            var startAngle = Math.Atan2(startVec.Y, startVec.X);
            var endAngle = Math.Atan2(endVec.Y, endVec.X);
            var angle = InterpolateAngle(startAngle, endAngle, element.Clockwise, ratio);
            return new Point3d(
                element.Center.X + element.Radius * Math.Cos(angle),
                element.Center.Y + element.Radius * Math.Sin(angle),
                element.Start.Z);
        }

        return null;
    }

    private static double InterpolateAngle(double start, double end, bool clockwise, double ratio)
    {
        start = Normalize(start);
        end = Normalize(end);
        if (clockwise)
        {
            if (end > start)
            {
                end -= Math.PI * 2;
            }

            return start + (end - start) * ratio;
        }

        if (end < start)
        {
            end += Math.PI * 2;
        }

        return start + (end - start) * ratio;
    }

    private static double Normalize(double angle)
    {
        while (angle < 0)
        {
            angle += Math.PI * 2;
        }

        while (angle >= Math.PI * 2)
        {
            angle -= Math.PI * 2;
        }

        return angle;
    }
}
