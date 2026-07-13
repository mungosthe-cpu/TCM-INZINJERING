using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class RoadDrawing
{
    public const string AxisLayerName = "TCM_OSOVINA";
    public const string StationLayerName = "TCM_STACIONAZA";
    public const string RadiusLayerName = "TCM_RADIJUS";
    public const string RegAppName = "TCM_INZINJERING";
    public const double DefaultLabelSideSign = 1.0;

    public static ObjectIdCollection DrawAxis(Transaction tr, BlockTableRecord modelSpace, RoadAxis axis)
    {
        EnsureLayer(tr, modelSpace.Database, AxisLayerName, Color.FromColorIndex(ColorMethod.ByAci, 1));
        EnsureLayer(tr, modelSpace.Database, StationLayerName, Color.FromColorIndex(ColorMethod.ByAci, 3));
        EnsureLayer(tr, modelSpace.Database, RadiusLayerName, Color.FromColorIndex(ColorMethod.ByAci, 2));
        EnsureRegApp(tr, modelSpace.Database);

        var ids = new ObjectIdCollection();
        var index = 0;
        foreach (var element in axis.Elements)
        {
            Entity entity = element.Type == AlignmentElementType.Tangent
                ? CreateLine(element)
                : CreateArc(element);

            entity.Layer = AxisLayerName;
            modelSpace.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
            RoadXData.AttachAxisElement(entity, axis.Name, index, element);
            ids.Add(entity.ObjectId);
            index++;
        }

        return ids;
    }

    public static int DrawStationLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        double interval,
        double tickLength,
        double textHeight,
        string prefix,
        double labelSideSign = DefaultLabelSideSign) =>
        DrawStationLabels(
            tr,
            modelSpace,
            axis,
            new StationLabelOptions
            {
                EqualIntervalInBounds = true,
                WholeInterval = true,
                StartStation = axis.StartStation,
                EndStation = axis.Elements[^1].EndStation,
                AlignToStart = true,
                LabelAtEnd = true,
                Interval = interval,
                TickLength = tickLength,
                TextHeight = textHeight,
                Prefix = prefix,
                LabelSideSign = labelSideSign
            });

    public static int DrawStationLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        StationLabelOptions options)
    {
        if (options.Interval <= 0)
        {
            return 0;
        }

        EnsureLayer(tr, modelSpace.Database, StationLayerName, Color.FromColorIndex(ColorMethod.ByAci, 3));
        EnsureRegApp(tr, modelSpace.Database);

        var count = 0;
        foreach (var station in CollectStationValues(axis, options).OrderBy(static s => s))
        {
            var point = axis.GetPointAtStation(station);
            var direction = axis.GetDirectionAtStation(station);
            if (point is null || direction is null)
            {
                continue;
            }

            var sideNormal = GetSideNormal(direction.Value, options.LabelSideSign);
            var halfTick = options.TickLength / 2.0;

            // Tick je centriran na osi i ide popreko nje.
            var tickStart = point.Value - sideNormal * halfTick;
            var tickEnd = point.Value + sideNormal * halfTick;

            var tick = new Line(tickStart, tickEnd)
            {
                Layer = StationLayerName,
                Color = Color.FromColorIndex(ColorMethod.ByLayer, 256)
            };
            modelSpace.AppendEntity(tick);
            tr.AddNewlyCreatedDBObject(tick, true);
            RoadXData.AttachStationLabel(tick, axis.Name, RoadXData.RoleTick, station);

            var textGap = options.TextHeight * 0.35;
            var textPosition = tickEnd + sideNormal * textGap;
            var textRotation = GetReadableTextRotation(sideNormal);
            var text = new DBText
            {
                Position = textPosition,
                Height = options.TextHeight,
                TextString = FormatStation(station, options.Prefix),
                Layer = StationLayerName,
                Color = Color.FromColorIndex(ColorMethod.ByLayer, 256),
                Rotation = textRotation
            };
            modelSpace.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
            RoadXData.AttachStationLabel(text, axis.Name, RoadXData.RoleText, station);

            count++;
        }

        return count;
    }

    private static IEnumerable<double> CollectStationValues(RoadAxis axis, StationLabelOptions options)
    {
        if (axis.Elements.Count == 0)
        {
            return Array.Empty<double>();
        }

        var stations = new HashSet<double>();
        var start = options.StartStation;
        var end = options.EndStation > start + 1e-6
            ? options.EndStation
            : axis.Elements[^1].EndStation;

        if (options.EqualIntervalInBounds && options.WholeInterval)
        {
            if (options.AlignToStart)
            {
                var station = start;
                while (station <= end + 1e-6)
                {
                    stations.Add(RoundStation(station));
                    station += options.Interval;
                }
            }
            else
            {
                var station = end;
                while (station >= start - 1e-6)
                {
                    stations.Add(RoundStation(station));
                    station -= options.Interval;
                }
            }
        }

        if (options.LabelAtStart)
        {
            stations.Add(RoundStation(start));
        }

        if (options.LabelAtEnd)
        {
            stations.Add(RoundStation(end));
        }

        if (options.LabelAtMainPoints)
        {
            foreach (var element in axis.Elements)
            {
                stations.Add(RoundStation(element.StartStation));
            }

            stations.Add(RoundStation(axis.Elements[^1].EndStation));
        }

        return stations.Where(s => s >= start - 1e-6 && s <= end + 1e-6);
    }

    private static double RoundStation(double station) => Math.Round(station, 6);

    public static int DrawRadiusLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        double textHeight,
        double labelSideSign = DefaultLabelSideSign)
    {
        EnsureLayer(tr, modelSpace.Database, RadiusLayerName, Color.FromColorIndex(ColorMethod.ByAci, 2));
        EnsureRegApp(tr, modelSpace.Database);

        var count = 0;
        var offset = textHeight * 1.5;
        var tickLength = textHeight * 0.5;
        var arrowLength = textHeight * 0.8;
        var arrowWidth = textHeight * 0.4;
        var arcIndex = 0;

        for (var i = 0; i < axis.Elements.Count; i++)
        {
            var element = axis.Elements[i];
            if (element.Type != AlignmentElementType.Arc || element.Radius < 1e-6)
            {
                continue;
            }

            var startDir = axis.GetDirectionAtStation(element.StartStation);
            var endDir = axis.GetDirectionAtStation(element.EndStation);
            if (startDir is null || endDir is null)
            {
                arcIndex++;
                continue;
            }

            var dimArc = CreateInnerDimArc(element, offset);
            dimArc.Layer = RadiusLayerName;
            modelSpace.AppendEntity(dimArc);
            tr.AddNewlyCreatedDBObject(dimArc, true);
            RoadXData.AttachRadiusAnnotation(dimArc, axis.Name, RoadXData.RoleRadiusDimArc, arcIndex, element.Radius);
            count++;

            count += DrawTangencyTick(
                tr, modelSpace, axis.Name, arcIndex, element.Start, startDir.Value, tickLength, element.Radius);
            count += DrawTangencyTick(
                tr, modelSpace, axis.Name, arcIndex, element.End, endDir.Value, tickLength, element.Radius);

            var pi = TryGetIntersectionPoint(axis, i);
            var leaderStartFrom = pi ?? element.Start - startDir.Value * (textHeight * 4.0);
            var leaderEndFrom = pi ?? element.End + endDir.Value * (textHeight * 4.0);

            count += DrawTangentLeader(
                tr,
                modelSpace,
                axis.Name,
                arcIndex,
                leaderStartFrom,
                element.Start,
                arrowLength,
                arrowWidth,
                element.Radius,
                RoadXData.RoleRadiusArrowStart);

            count += DrawTangentLeader(
                tr,
                modelSpace,
                axis.Name,
                arcIndex,
                leaderEndFrom,
                element.End,
                arrowLength,
                arrowWidth,
                element.Radius,
                RoadXData.RoleRadiusArrowEnd);

            count += DrawConnectorToDimArc(
                tr,
                modelSpace,
                axis.Name,
                arcIndex,
                dimArc.StartPoint,
                element.Start,
                startDir.Value,
                element.Radius);

            count += DrawConnectorToDimArc(
                tr,
                modelSpace,
                axis.Name,
                arcIndex,
                dimArc.EndPoint,
                element.End,
                endDir.Value,
                element.Radius);

            var midStation = (element.StartStation + element.EndStation) / 2.0;
            var midDir = axis.GetDirectionAtStation(midStation);
            if (midDir is null)
            {
                arcIndex++;
                continue;
            }

            var textPosition = GetInnerDimArcMidpoint(element, offset);
            var text = new DBText
            {
                Position = textPosition,
                Height = textHeight,
                TextString = FormatRadius(element.Radius),
                Layer = RadiusLayerName,
                Rotation = Math.Atan2(midDir.Value.Y, midDir.Value.X)
            };
            modelSpace.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
            RoadXData.AttachRadiusAnnotation(text, axis.Name, RoadXData.RoleRadiusText, arcIndex, element.Radius);
            count++;

            arcIndex++;
        }

        return count;
    }

    public static void SaveAxisMetadata(
        Transaction tr,
        Database db,
        RoadAxis axis,
        StationLabelOptions options,
        double curveRadius)
    {
        RoadAxisStore.Save(tr, db, new RoadAxisMetadata
        {
            Name = axis.Name,
            StartStation = axis.StartStation,
            EndStation = options.EndStation,
            Interval = options.Interval,
            TickLength = options.TickLength,
            TextHeight = options.TextHeight,
            Prefix = options.Prefix,
            LabelSideSign = options.LabelSideSign,
            CurveRadius = curveRadius,
            EqualIntervalInBounds = options.EqualIntervalInBounds,
            WholeInterval = options.WholeInterval,
            AlignToStart = options.AlignToStart,
            LabelAtStart = options.LabelAtStart,
            LabelAtEnd = options.LabelAtEnd,
            LabelAtMainPoints = options.LabelAtMainPoints
        });
    }

    public static void SaveAxisMetadata(
        Transaction tr,
        Database db,
        RoadAxis axis,
        double interval,
        double tickLength,
        double textHeight,
        string prefix,
        double curveRadius,
        double labelSideSign = DefaultLabelSideSign) =>
        SaveAxisMetadata(
            tr,
            db,
            axis,
            new StationLabelOptions
            {
                EqualIntervalInBounds = true,
                WholeInterval = true,
                StartStation = axis.StartStation,
                EndStation = axis.Elements[^1].EndStation,
                AlignToStart = true,
                LabelAtEnd = true,
                Interval = interval,
                TickLength = tickLength,
                TextHeight = textHeight,
                Prefix = prefix,
                LabelSideSign = labelSideSign
            },
            curveRadius);

    private static Vector3d GetSideNormal(Vector3d direction, double labelSideSign)
    {
        var leftNormal = new Vector3d(-direction.Y, direction.X, 0);
        if (leftNormal.Length < 1e-9)
        {
            return Vector3d.YAxis * labelSideSign;
        }

        return leftNormal.GetNormal() * labelSideSign;
    }

    private static Line CreateLine(AlignmentElement element) =>
        new(element.Start, element.End);

    private static Arc CreateArc(AlignmentElement element)
    {
        var startVec = element.Start - element.Center;
        var endVec = element.End - element.Center;
        var startAngle = Math.Atan2(startVec.Y, startVec.X);
        var endAngle = Math.Atan2(endVec.Y, endVec.X);

        return element.Clockwise
            ? new Arc(element.Center, element.Radius, endAngle, startAngle)
            : new Arc(element.Center, element.Radius, startAngle, endAngle);
    }

    private static void EnsureRegApp(Transaction tr, Database db)
    {
        var regTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (regTable.Has(RegAppName))
        {
            return;
        }

        regTable.UpgradeOpen();
        var record = new RegAppTableRecord { Name = RegAppName };
        regTable.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static void EnsureLayer(Transaction tr, Database db, string layerName, Color color)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(layerName))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = layerName,
            Color = color
        };
        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    public static string FormatStation(double station, string prefix)
    {
        var kilometers = (int)Math.Floor(station / 1000.0);
        var meters = station - kilometers * 1000.0;
        var labelPrefix = string.IsNullOrWhiteSpace(prefix) ? "STA" : prefix.Trim();
        return $"{labelPrefix} {kilometers}+{meters:000.00}";
    }

    private static double GetReadableTextRotation(Vector3d sideNormal)
    {
        var rotation = Math.Atan2(sideNormal.Y, sideNormal.X);
        if (rotation > Math.PI / 2.0)
        {
            rotation -= Math.PI;
        }
        else if (rotation < -Math.PI / 2.0)
        {
            rotation += Math.PI;
        }

        return rotation;
    }

    public static string FormatRadius(double radius) => $"R={radius:F2}";

    private static Point3d? TryGetIntersectionPoint(RoadAxis axis, int arcIndex)
    {
        if (arcIndex <= 0 || arcIndex >= axis.Elements.Count - 1)
        {
            return null;
        }

        var prev = axis.Elements[arcIndex - 1];
        var next = axis.Elements[arcIndex + 1];
        if (prev.Type != AlignmentElementType.Tangent || next.Type != AlignmentElementType.Tangent)
        {
            return null;
        }

        var p0 = TangentArcGeometry.To2d(prev.Start);
        var p1 = TangentArcGeometry.To2d(prev.End);
        var p2 = TangentArcGeometry.To2d(next.Start);
        var p3 = TangentArcGeometry.To2d(next.End);

        return TangentArcGeometry.TryIntersectLines(p0, p1, p2, p3, out var pi)
            ? TangentArcGeometry.To3d(pi)
            : null;
    }

    private static Arc CreateInnerDimArc(AlignmentElement element, double offset)
    {
        var startVec = element.Start - element.Center;
        var endVec = element.End - element.Center;
        var startAngle = Math.Atan2(startVec.Y, startVec.X);
        var endAngle = Math.Atan2(endVec.Y, endVec.X);
        var dimRadius = Math.Max(element.Radius - offset, element.Radius * 0.5);

        return element.Clockwise
            ? new Arc(element.Center, dimRadius, endAngle, startAngle)
            : new Arc(element.Center, dimRadius, startAngle, endAngle);
    }

    private static Point3d GetInnerDimArcMidpoint(AlignmentElement element, double offset)
    {
        var startVec = element.Start - element.Center;
        var endVec = element.End - element.Center;
        var startAngle = Math.Atan2(startVec.Y, startVec.X);
        var endAngle = Math.Atan2(endVec.Y, endVec.X);
        double midAngle;

        if (element.Clockwise)
        {
            if (endAngle > startAngle)
            {
                endAngle -= Math.PI * 2;
            }

            midAngle = startAngle + (endAngle - startAngle) / 2.0;
        }
        else
        {
            if (endAngle < startAngle)
            {
                endAngle += Math.PI * 2;
            }

            midAngle = startAngle + (endAngle - startAngle) / 2.0;
        }

        var dimRadius = Math.Max(element.Radius - offset, element.Radius * 0.5);
        return new Point3d(
            element.Center.X + dimRadius * Math.Cos(midAngle),
            element.Center.Y + dimRadius * Math.Sin(midAngle),
            element.Start.Z);
    }

    private static int DrawTangencyTick(
        Transaction tr,
        BlockTableRecord modelSpace,
        string axisName,
        int arcIndex,
        Point3d point,
        Vector3d direction,
        double tickLength,
        double radius)
    {
        var normal = GetSideNormal(direction, 1.0);
        var half = tickLength / 2.0;
        AppendRadiusLine(
            tr,
            modelSpace,
            point - normal * half,
            point + normal * half,
            axisName,
            arcIndex,
            radius,
            RoadXData.RoleRadiusTick,
            null);
        return 1;
    }

    private static int DrawTangentLeader(
        Transaction tr,
        BlockTableRecord modelSpace,
        string axisName,
        int arcIndex,
        Point3d from,
        Point3d to,
        double arrowLength,
        double arrowWidth,
        double radius,
        string role)
    {
        AppendRadiusLine(tr, modelSpace, from, to, axisName, arcIndex, radius, role, 7);

        var direction = to - from;
        if (direction.Length < 1e-9)
        {
            return 1;
        }

        var dir = direction.GetNormal();
        var perp = new Vector3d(-dir.Y, dir.X, 0);
        var basePoint = to - dir * arrowLength;
        var left = basePoint + perp * (arrowWidth / 2.0);
        var right = basePoint - perp * (arrowWidth / 2.0);

        AppendRadiusLine(tr, modelSpace, to, left, axisName, arcIndex, radius, role, 7);
        AppendRadiusLine(tr, modelSpace, to, right, axisName, arcIndex, radius, role, 7);
        return 3;
    }

    private static int DrawConnectorToDimArc(
        Transaction tr,
        BlockTableRecord modelSpace,
        string axisName,
        int arcIndex,
        Point3d dimPoint,
        Point3d tangencyPoint,
        Vector3d tangentDir,
        double radius)
    {
        if (tangentDir.Length < 1e-9)
        {
            return 0;
        }

        var dir = tangentDir.GetNormal();
        var along = tangencyPoint - dimPoint;
        var projectedLength = along.DotProduct(dir);
        var foot = dimPoint + dir * projectedLength;
        if (foot.DistanceTo(tangencyPoint) < 1e-4)
        {
            return 0;
        }

        AppendRadiusLine(tr, modelSpace, dimPoint, foot, axisName, arcIndex, radius, RoadXData.RoleRadiusDimArc, null);
        return 1;
    }

    private static void AppendRadiusLine(
        Transaction tr,
        BlockTableRecord modelSpace,
        Point3d start,
        Point3d end,
        string axisName,
        int arcIndex,
        double radius,
        string role,
        short? colorIndex)
    {
        var line = new Line(start, end) { Layer = RadiusLayerName };
        if (colorIndex is not null)
        {
            line.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex.Value);
        }

        modelSpace.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        RoadXData.AttachRadiusAnnotation(line, axisName, role, arcIndex, radius);
    }
}
