using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class RoadDrawing
{
    public const string AxisLayerName = "TCM_OSOVINA";
    public const string StationLayerName = "TCM_STACIONAZA";
    public const string RadiusLayerName = "TCM_RADIJUS";
    public const string SegmentLayerName = "TCM_SEGMENT";
    public const string TableLayerName = "TCM_TABELA";
    public const string RegAppName = "TCM_INZINJERING";
    public const double DefaultLabelSideSign = 1.0; // +1 = leva strana u smeru rasta stacionaze
    public const double DefaultTickLength = 15.0;
    public const double StationTextGapFromTick = 1.0;

    public static ObjectIdCollection DrawAxis(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        ObjectId sourcePolylineId = default,
        short axisColorIndex = DrawingColorDefaults.Axis)
    {
        ObjectIdCollection? ids = null;
        RunWithUnlockedAxisLayer(tr, modelSpace.Database, () =>
        {
            ids = DrawAxisCore(tr, modelSpace, axis, sourcePolylineId, axisColorIndex);
        });

        return ids ?? new ObjectIdCollection();
    }

    public static void RunWithUnlockedAxisLayer(Transaction tr, Database db, Action action)
    {
        PrepareAxisLayer(tr, db);
        UnlockAxisLayer(tr, db);
        try
        {
            action();
        }
        finally
        {
            LockAxisLayer(tr, db);
        }
    }

    public static void EnsureAxisLayerPickThrough(Transaction tr, Database db)
    {
        PrepareAxisLayer(tr, db);
        LockAxisLayer(tr, db);
    }

    internal static ObjectIdCollection DrawAxisCore(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        ObjectId sourcePolylineId,
        short axisColorIndex = DrawingColorDefaults.Axis)
    {
        EnsureLayer(tr, modelSpace.Database, StationLayerName, Color.FromColorIndex(ColorMethod.ByAci, 3));
        EnsureLayer(tr, modelSpace.Database, RadiusLayerName, Color.FromColorIndex(ColorMethod.ByAci, 2));
        EnsureLayer(tr, modelSpace.Database, SegmentLayerName, Color.FromColorIndex(ColorMethod.ByAci, 4));
        EnsureLayer(tr, modelSpace.Database, TableLayerName, Color.FromColorIndex(ColorMethod.ByAci, 7));
        EnsureRegApp(tr, modelSpace.Database);

        var ids = new ObjectIdCollection();
        var index = 0;
        foreach (var element in axis.Elements)
        {
            Entity entity = element.Type == AlignmentElementType.Tangent
                ? CreateLine(element)
                : CreateArc(element);

            entity.Layer = AxisLayerName;
            entity.Color = ToAciColor(axisColorIndex);
            modelSpace.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
            RoadXData.AttachAxisElement(entity, axis.Name, index, element);
            ids.Add(entity.ObjectId);
            index++;
        }

        if (!sourcePolylineId.IsNull)
        {
            PlaceAxisBelowSourcePolyline(tr, modelSpace, ids, sourcePolylineId);
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
        StationFontPreferences.Load();
        var textStyleId = StationTextStyleHelper.Ensure(tr, modelSpace.Database, StationFontPreferences.FontFileName);

        var count = 0;
        foreach (var station in CollectStationValues(axis, options).OrderBy(static s => s))
        {
            var point = axis.GetPointAtStation(station);
            var direction = axis.SampleDirectionAtStation(station);
            if (point is null || direction is null)
            {
                continue;
            }

            // Leva strana polilinije gledano u smeru rasta stacionaze (LabelSideSign = -1 -> desna).
            var sideNormal = GetSideNormal(direction.Value, options.LabelSideSign);
            var halfTick = options.TickLength / 2.0;
            // Unutar krivine (PC..PT): ista boja kao oznaka radijusa L/R.
            var onCurve = IsStationWithinArc(axis, station);
            var markColor = onCurve
                ? ToAciColor(options.SegmentLabelColorIndex)
                : ToAciColor(options.StationTickColorIndex);
            var textColor = onCurve
                ? ToAciColor(options.SegmentLabelColorIndex)
                : ToAciColor(options.StationTextColorIndex);

            // Tick je centriran na osi; tickEnd je na strani oznake (levo).
            var tickStart = point.Value - sideNormal * halfTick;
            var tickEnd = point.Value + sideNormal * halfTick;

            var tick = new Line(tickStart, tickEnd)
            {
                Layer = StationLayerName,
                Color = markColor
            };
            modelSpace.AppendEntity(tick);
            tr.AddNewlyCreatedDBObject(tick, true);
            RoadXData.AttachStationLabel(tick, axis.Name, RoadXData.RoleTick, station);

            var stationCounter = options.AxisCounterStart + count;
            var relativeStation = Math.Max(0, station - options.StartStation);
            var textRotation = GetPerpendicularLabelRotation(direction.Value, options.LabelSideSign);

            if (options.LabelFormat == StationLabelFormat.ChainageOnly)
            {
                var labelText = FormatStation(relativeStation, options.Prefix, options.ChainageFormat);
                var estimatedWidth = EstimateTextWidth(labelText, options.TextHeight);
                var textPosition = tickEnd + sideNormal * (StationTextGapFromTick + estimatedWidth);
                var text = CreateStationDbText(
                    labelText,
                    textPosition,
                    options.TextHeight,
                    textColor,
                    textRotation,
                    textStyleId,
                    modelSpace.Database);
                modelSpace.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                RoadXData.AttachStationLabel(text, axis.Name, RoadXData.RoleText, station);
            }
            else
            {
                // Dva reda (kao na default TCMPOPOSPOZ): "OSA 10" iznad, "0-180.00" ispod,
                // upravno na osovinu, pored štapića — van polilinije.
                var namePart = FormatStaAttribute(options.Prefix, stationCounter);
                var chainagePart = FormatChainage(relativeStation, options.ChainageFormat);
                var lineSpacing = options.TextHeight * 1.35;
                var roadDir = direction.Value;
                if (roadDir.Length > 1e-9)
                {
                    roadDir = roadDir.GetNormal();
                }

                var basePos = tickEnd + sideNormal * (StationTextGapFromTick + options.TextHeight * 0.25);
                var namePosition = basePos - roadDir * (lineSpacing * 0.5);
                var chainagePosition = basePos + roadDir * (lineSpacing * 0.5);

                var nameText = CreateStationDbText(
                    namePart,
                    namePosition,
                    options.TextHeight,
                    textColor,
                    textRotation,
                    textStyleId,
                    modelSpace.Database);
                modelSpace.AppendEntity(nameText);
                tr.AddNewlyCreatedDBObject(nameText, true);
                RoadXData.AttachStationLabel(nameText, axis.Name, RoadXData.RoleText, station);

                var chainageText = CreateStationDbText(
                    chainagePart,
                    chainagePosition,
                    options.TextHeight,
                    textColor,
                    textRotation,
                    textStyleId,
                    modelSpace.Database);
                modelSpace.AppendEntity(chainageText);
                tr.AddNewlyCreatedDBObject(chainageText, true);
                RoadXData.AttachStationLabel(chainageText, axis.Name, RoadXData.RoleChainage, station);
            }

            count++;
        }

        return count;
    }

    public static IReadOnlyList<double> CollectStationsForSync(RoadAxis axis, StationLabelOptions options) =>
        CollectStationValues(axis, options).OrderBy(static s => s).ToList();

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

        // Uvek ubaci pocetak/kraj lukova (PC/PT) — oznake geometrije krivine.
        foreach (var boundary in GetArcBoundaryStations(axis))
        {
            stations.Add(boundary);
        }

        if (options.LabelAtMainPoints)
        {
            foreach (var element in axis.Elements)
            {
                stations.Add(RoundStation(element.StartStation));
            }

            stations.Add(RoundStation(axis.Elements[^1].EndStation));
        }

        var result = stations
            .Where(s => s >= start - 1e-6 && s <= end + 1e-6)
            .OrderBy(static s => s)
            .ToList();
        PruneCrowdedNearCurveBoundaries(result, options, GetArcBoundaryStations(axis));
        PruneCrowdedEndStation(result, options, end);
        return result;
    }

    private static HashSet<double> GetArcBoundaryStations(RoadAxis axis)
    {
        var boundaries = new HashSet<double>();
        foreach (var element in axis.Elements)
        {
            if (element.Type != AlignmentElementType.Arc || element.Radius < 1e-6)
            {
                continue;
            }

            boundaries.Add(RoundStation(element.StartStation));
            boundaries.Add(RoundStation(element.EndStation));
        }

        return boundaries;
    }

    /// <summary>
    /// True ako je stacionaza na krivini (ukljucujuci pocetak i kraj luka).
    /// </summary>
    private static bool IsStationWithinArc(RoadAxis axis, double station, double tolerance = 1e-3)
    {
        foreach (var element in axis.Elements)
        {
            if (element.Type != AlignmentElementType.Arc || element.Radius < 1e-6)
            {
                continue;
            }

            if (station >= element.StartStation - tolerance && station <= element.EndStation + tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNearStation(IEnumerable<double> stations, double value, double tolerance = 1e-3) =>
        stations.Any(station => Math.Abs(station - value) <= tolerance);

    /// <summary>
    /// Ako je intervalna stacionaza bliza PC/PT krivine (manje od interval/2),
    /// ne crtaj intervalnu — ostaje oznaka pocetka/kraja krivine.
    /// </summary>
    private static void PruneCrowdedNearCurveBoundaries(
        List<double> stations,
        StationLabelOptions options,
        IReadOnlyCollection<double> curveBoundaries)
    {
        const double tolerance = 1e-3;
        if (stations.Count < 2 || options.Interval <= 0 || curveBoundaries.Count == 0)
        {
            return;
        }

        var minGap = options.Interval / 2.0;
        var toRemove = new List<double>();
        foreach (var station in stations)
        {
            if (IsNearStation(curveBoundaries, station, tolerance))
            {
                continue;
            }

            foreach (var boundary in curveBoundaries)
            {
                if (Math.Abs(station - boundary) < minGap - tolerance)
                {
                    toRemove.Add(station);
                    break;
                }
            }
        }

        foreach (var station in toRemove)
        {
            stations.Remove(station);
        }
    }

    /// <summary>
    /// Ako je krajnja stacionaza blizu predposlednje (manje od interval/2), ne crtaj predposlednju.
    /// </summary>
    private static void PruneCrowdedEndStation(List<double> stations, StationLabelOptions options, double end)
    {
        const double tolerance = 1e-3;
        if (!options.LabelAtEnd || stations.Count < 2 || options.Interval <= 0)
        {
            return;
        }

        var last = stations[^1];
        if (Math.Abs(last - end) > tolerance)
        {
            return;
        }

        var penultimate = stations[^2];
        var gap = last - penultimate;
        if (gap >= options.Interval / 2.0 - tolerance)
        {
            return;
        }

        if (options.LabelAtStart && Math.Abs(penultimate - options.StartStation) < tolerance)
        {
            return;
        }

        stations.RemoveAt(stations.Count - 2);
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

            arcIndex++;
        }

        return count;
    }

    public static int DrawSegmentLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        double textHeight,
        double labelSideSign = DefaultLabelSideSign,
        short segmentColorIndex = DrawingColorDefaults.SegmentLabel)
    {
        EnsureLayer(tr, modelSpace.Database, SegmentLayerName, Color.FromColorIndex(ColorMethod.ByAci, 4));
        EnsureRegApp(tr, modelSpace.Database);

        var count = 0;
        for (var i = 0; i < axis.Elements.Count; i++)
        {
            var element = axis.Elements[i];
            var midStation = (element.StartStation + element.EndStation) / 2.0;
            var point = axis.GetPointAtStation(midStation);
            var direction = axis.GetDirectionAtStation(midStation);
            if (point is null || direction is null)
            {
                continue;
            }

            var labelText = FormatSegmentLabel(element);
            var rotation = Math.Atan2(direction.Value.Y, direction.Value.X);
            var position = GetHorizontallyCenteredPosition(point.Value, labelText, textHeight, rotation);
            var text = new DBText
            {
                Position = position,
                Height = textHeight,
                TextString = labelText,
                Layer = SegmentLayerName,
                Color = ToAciColor(segmentColorIndex),
                Rotation = rotation
            };
            modelSpace.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
            RoadXData.AttachSegmentLabel(text, axis.Name, i);
            count++;
        }

        return count;
    }

    public static int DrawAxisTable(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        Point3d insertionPoint,
        double textHeight)
    {
        EnsureLayer(tr, modelSpace.Database, TableLayerName, Color.FromColorIndex(ColorMethod.ByAci, 7));
        EnsureRegApp(tr, modelSpace.Database);

        var rows = axis.Elements.Count + 3;
        var cols = 6;
        var rowHeight = textHeight * 2.2;
        var colWidths = new[] { textHeight * 4, textHeight * 8, textHeight * 9, textHeight * 9, textHeight * 8, textHeight * 8 };
        var width = colWidths.Sum();
        var height = rows * rowHeight;

        var count = 0;
        AppendTableLine(tr, modelSpace, insertionPoint, insertionPoint + Vector3d.XAxis * width, axis.Name);
        count++;
        for (var row = 1; row <= rows; row++)
        {
            var y = -row * rowHeight;
            AppendTableLine(
                tr,
                modelSpace,
                insertionPoint + Vector3d.YAxis * y,
                insertionPoint + Vector3d.XAxis * width + Vector3d.YAxis * y,
                axis.Name);
            count++;
        }

        var x = 0.0;
        for (var col = 0; col <= cols; col++)
        {
            AppendTableLine(
                tr,
                modelSpace,
                insertionPoint + Vector3d.XAxis * x,
                insertionPoint + Vector3d.XAxis * x - Vector3d.YAxis * height,
                axis.Name);
            count++;

            if (col < cols)
            {
                x += colWidths[col];
            }
        }

        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 0, 0, $"OSOVINA {axis.Name}", textHeight, axis.Name, 2);
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 0, 1, "Br.", textHeight, axis.Name);
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 1, 1, "Tip", textHeight, axis.Name);
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 2, 1, "STA od", textHeight, axis.Name);
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 3, 1, "STA do", textHeight, axis.Name);
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 4, 1, "L [m]", textHeight, axis.Name);
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 5, 1, "R [m]", textHeight, axis.Name);
        count += 7;

        for (var i = 0; i < axis.Elements.Count; i++)
        {
            var element = axis.Elements[i];
            var row = i + 2;
            AppendTableText(tr, modelSpace, insertionPoint, colWidths, 0, row, (i + 1).ToString(), textHeight, axis.Name);
            AppendTableText(tr, modelSpace, insertionPoint, colWidths, 1, row, element.Type == AlignmentElementType.Tangent ? "Pravac" : "Luk", textHeight, axis.Name);
            AppendTableText(tr, modelSpace, insertionPoint, colWidths, 2, row, FormatStation(element.StartStation, string.Empty), textHeight, axis.Name);
            AppendTableText(tr, modelSpace, insertionPoint, colWidths, 3, row, FormatStation(element.EndStation, string.Empty), textHeight, axis.Name);
            AppendTableText(tr, modelSpace, insertionPoint, colWidths, 4, row, element.Length.ToString("F2"), textHeight, axis.Name);
            AppendTableText(tr, modelSpace, insertionPoint, colWidths, 5, row, element.Type == AlignmentElementType.Arc ? element.Radius.ToString("F2") : "-", textHeight, axis.Name);
            count += 6;
        }

        var totalRow = axis.Elements.Count + 2;
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 1, totalRow, "Ukupno", textHeight, axis.Name);
        AppendTableText(tr, modelSpace, insertionPoint, colWidths, 4, totalRow, axis.TotalLength.ToString("F2"), textHeight, axis.Name);
        count += 2;

        return count;
    }

    public static void SaveAxisMetadata(
        Transaction tr,
        Database db,
        RoadAxis axis,
        StationLabelOptions options,
        double curveRadius,
        ObjectId sourcePolylineId = default,
        double polylineStartDistance = 0,
        double polylineEndDistance = 0)
    {
        long sourceHandle = 0;
        double polylineReferenceLength = 0;
        if (!sourcePolylineId.IsNull)
        {
            var source = (DBObject)tr.GetObject(sourcePolylineId, OpenMode.ForRead);
            sourceHandle = source.Handle.Value;
            if (source is Polyline polyline)
            {
                polylineReferenceLength = polyline.Length;
            }
        }

        RoadAxisStore.Save(tr, db, new RoadAxisMetadata
        {
            Name = axis.Name,
            StartStation = options.StartStation,
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
            LabelAtMainPoints = options.LabelAtMainPoints,
            SourcePolylineHandle = sourceHandle,
            PolylineStartDistance = polylineStartDistance,
            PolylineEndDistance = polylineEndDistance,
            PolylineReferenceLength = polylineReferenceLength,
            AxisCounterStart = options.AxisCounterStart,
            LabelFormat = options.LabelFormat,
            ChainageFormat = options.ChainageFormat,
            DrawSegmentLabels = options.DrawSegmentLabels,
            AxisColorIndex = options.AxisColorIndex,
            StationTextColorIndex = options.StationTextColorIndex,
            StationTickColorIndex = options.StationTickColorIndex,
            SegmentLabelColorIndex = options.SegmentLabelColorIndex
        });
    }

    private static Color ToAciColor(short aci) =>
        Color.FromColorIndex(ColorMethod.ByAci, aci);

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
                LabelSideSign = labelSideSign,
                AxisCounterStart = 1,
                LabelFormat = StationLabelFormat.ProjectCounter
            },
            curveRadius);

    private static DBText CreateStationDbText(
        string contents,
        Point3d alignmentPoint,
        double height,
        Color color,
        double rotation,
        ObjectId textStyleId,
        Database db)
    {
        var text = new DBText
        {
            Height = height,
            TextString = contents,
            Layer = StationLayerName,
            Color = color,
            Rotation = rotation,
            TextStyleId = textStyleId,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid
        };
        text.AlignmentPoint = alignmentPoint;
        text.AdjustAlignment(db);
        return text;
    }

    private static Vector3d GetSideNormal(Vector3d direction, double labelSideSign)
    {
        if (Math.Abs(labelSideSign) < 1e-9)
        {
            labelSideSign = DefaultLabelSideSign;
        }

        // Leva normala u odnosu na smer rasta stacionaze (desna za labelSideSign < 0).
        var leftNormal = new Vector3d(-direction.Y, direction.X, 0);
        if (leftNormal.Length < 1e-9)
        {
            return Vector3d.YAxis * Math.Sign(labelSideSign);
        }

        return leftNormal.GetNormal() * Math.Sign(labelSideSign);
    }

    private static double GetPerpendicularLabelRotation(Vector3d direction, double labelSideSign)
    {
        if (Math.Abs(labelSideSign) < 1e-9)
        {
            labelSideSign = DefaultLabelSideSign;
        }

        // Upravno na osovinu, orijentacija u smeru crtanja polilinije (rast stacionaze).
        return Math.Atan2(direction.Y, direction.X) - Math.PI / 2.0 * Math.Sign(labelSideSign);
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

    internal static void EnsureRegApp(Transaction tr, Database db)
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

    private static void PrepareAxisLayer(Transaction tr, Database db)
    {
        EnsureLayer(tr, db, AxisLayerName, Color.FromColorIndex(ColorMethod.ByAci, 1));
    }

    private static void UnlockAxisLayer(Transaction tr, Database db)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var layer = (LayerTableRecord)tr.GetObject(layerTable[AxisLayerName], OpenMode.ForWrite);
        layer.IsLocked = false;
    }

    private static void LockAxisLayer(Transaction tr, Database db)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layerTable.Has(AxisLayerName))
        {
            return;
        }

        var layer = (LayerTableRecord)tr.GetObject(layerTable[AxisLayerName], OpenMode.ForWrite);
        layer.IsLocked = true;
    }

    private static void PlaceAxisBelowSourcePolyline(
        Transaction tr,
        BlockTableRecord modelSpace,
        ObjectIdCollection axisIds,
        ObjectId sourcePolylineId)
    {
        if (axisIds.Count == 0 || sourcePolylineId.IsNull || modelSpace.DrawOrderTableId.IsNull)
        {
            return;
        }

        var drawOrder = (DrawOrderTable)tr.GetObject(modelSpace.DrawOrderTableId, OpenMode.ForWrite);
        drawOrder.MoveBelow(axisIds, sourcePolylineId);
    }

    public static string FormatStaAttribute(string prefix, int stationCounter)
    {
        var labelPrefix = prefix?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(labelPrefix)
            ? stationCounter.ToString()
            : $"{labelPrefix} {stationCounter}";
    }

    public static string FormatStation(double station, string prefix, int chainageFormat = ChainageFormatter.DefaultFormat)
    {
        var chainage = FormatChainage(station, chainageFormat);
        var labelPrefix = prefix?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(labelPrefix) ? chainage : $"{labelPrefix} {chainage}";
    }

    public static string FormatProjectStation(double station, string prefix, int stationCounter, int chainageFormat = ChainageFormatter.DefaultFormat) =>
        $"{FormatStaAttribute(prefix, stationCounter)} {FormatChainage(station, chainageFormat)}";

    private static string FormatChainage(double station, int chainageFormat = ChainageFormatter.DefaultFormat) =>
        ChainageFormatter.Format(station, chainageFormat);

    public static string FormatRadius(double radius) => $"R={radius:F2}";

    private static string FormatSegmentLabel(AlignmentElement element)
    {
        return element.Type == AlignmentElementType.Arc
            ? $"L={element.Length:F2} R={element.Radius:F2}"
            : $"L={element.Length:F2}";
    }

    public static double EstimateTextWidth(string text, double height)
    {
        return height * text.Length * 0.55;
    }

    private static Point3d GetHorizontallyCenteredPosition(
        Point3d center,
        string text,
        double height,
        double rotation)
    {
        var width = EstimateTextWidth(text, height);
        var textDir = new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0);
        return center - textDir * (width * 0.5);
    }

    private static void AppendTableLine(
        Transaction tr,
        BlockTableRecord modelSpace,
        Point3d start,
        Point3d end,
        string axisName)
    {
        var line = new Line(start, end)
        {
            Layer = TableLayerName,
            Color = Color.FromColorIndex(ColorMethod.ByLayer, 256)
        };
        modelSpace.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        RoadXData.AttachAxisTable(line, axisName);
    }

    private static void AppendTableText(
        Transaction tr,
        BlockTableRecord modelSpace,
        Point3d insertionPoint,
        double[] colWidths,
        int col,
        int row,
        string value,
        double textHeight,
        string axisName,
        int colSpan = 1)
    {
        var x = colWidths.Take(col).Sum() + textHeight * 0.4;
        var y = -(row + 0.7) * textHeight * 2.2;
        var text = new DBText
        {
            Position = insertionPoint + Vector3d.XAxis * x + Vector3d.YAxis * y,
            Height = textHeight,
            TextString = value,
            Layer = TableLayerName,
            Color = Color.FromColorIndex(ColorMethod.ByLayer, 256),
            Rotation = 0
        };
        modelSpace.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
        RoadXData.AttachAxisTable(text, axisName);
    }

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
