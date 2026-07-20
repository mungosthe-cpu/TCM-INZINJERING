using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Roads.CrossAxis;

namespace TcmInzenjering.Plugin.Roads;

internal static class RoadDrawing
{
    public const string AxisLayerName = "TCM_OSOVINA";
    public const string StationLayerName = "TCM_STACIONAZA";
    public const string RadiusLayerName = "TCM_RADIJUS";
    public const string RadiusDimStyleName = "TCM-Radijus";
    public const string SegmentLayerName = "TCM_SEGMENT";
    public const string TableLayerName = "TCM_TABELA";
    public const string TangentNodeLayerName = "TCM_CVOR";
    public const string SourcePolylineLayerName = "TCM_TANG_POLIGON";
    public const string ProjectedAxisLayerName = "TCM_OSOVINA_3D";
    public const string DashedLinetypeName = "DASHED";
    /// <summary>Odmak od čvora (PI) do bliže ivice tabele duž bisektrise ugla tangenti (kao na referentnom crtežu).</summary>
    public const double DefaultTangentNodeTableOffset = 25.0;
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
        EnsureLayer(tr, modelSpace.Database, RadiusLayerName, Color.FromColorIndex(ColorMethod.ByAci, 1));
        EnsureLayer(tr, modelSpace.Database, SegmentLayerName, Color.FromColorIndex(ColorMethod.ByAci, 4));
        EnsureLayer(tr, modelSpace.Database, TableLayerName, Color.FromColorIndex(ColorMethod.ByAci, 7));
        EnsureLayer(tr, modelSpace.Database, TangentNodeLayerName, Color.FromColorIndex(ColorMethod.ByAci, 7));
        EnsureRegApp(tr, modelSpace.Database);

        var ids = new ObjectIdCollection();
        var index = 0;
        foreach (var element in axis.Elements)
        {
            Entity entity = element.Type switch
            {
                AlignmentElementType.Tangent => CreateLine(element),
                AlignmentElementType.Spiral => CreateSpiralPolyline(element),
                _ => CreateArc(element)
            };

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

        RoadEntityIndex.Invalidate();
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

            // Levo/desno od osovine u smeru rasta stacionaze (nezavisno od strane ispisa oznaka).
            var leftNormal = GetSideNormal(direction.Value, 1.0);
            var labelSideNormal = GetSideNormal(direction.Value, options.LabelSideSign);
            // Tick: ukupna dužina iz dijaloga (TickLength), podeljeno levo/desno.
            // Ne koristi StationFontPreferences (to je za pop. ose / TCMSTACFONT).
            var half = Math.Max(0.05, options.TickLength * 0.5);
            var leftLength = half;
            var rightLength = half;
            // Unutar krivine (PC..PT): ista boja kao oznaka radijusa L/R.
            var onCurve = IsStationWithinArc(axis, station);
            var markColor = onCurve
                ? ToAciColor(options.SegmentLabelColorIndex)
                : ToAciColor(options.StationTickColorIndex);
            var textColor = onCurve
                ? ToAciColor(options.SegmentLabelColorIndex)
                : ToAciColor(options.StationTextColorIndex);

            // Tick: levo = +leftNormal, desno = -leftNormal.
            var tickStart = point.Value - leftNormal * rightLength;
            var tickEnd = point.Value + leftNormal * leftLength;
            // Anker za tekst na strani oznake.
            var textTickEnd = options.LabelSideSign >= 0 ? tickEnd : tickStart;
            var textSideNormal = labelSideNormal;

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
                var textPosition = textTickEnd + textSideNormal * (StationTextGapFromTick + estimatedWidth);
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

                var basePos = textTickEnd + textSideNormal * (StationTextGapFromTick + options.TextHeight * 0.25);
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

    /// <summary>
    /// Jedna ručno dodata poprečna osa — isti sloj, boja, font i raspored kao intervalne stacionaže.
    /// </summary>
    internal static Line DrawManualCrossAxisStation(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        StationLabelOptions options,
        string axisName,
        double station,
        double leftWidth,
        double rightWidth,
        string namePart,
        int crossAxisNumber)
    {
        var point = axis.GetPointAtStation(station);
        var direction = axis.SampleDirectionAtStation(station);
        if (point is null || direction is null)
        {
            throw new InvalidOperationException("Nevalidna stacionaža za poprečnu osu.");
        }

        EnsureLayer(tr, modelSpace.Database, StationLayerName, Color.FromColorIndex(ColorMethod.ByAci, 3));
        EnsureRegApp(tr, modelSpace.Database);
        StationFontPreferences.Load();
        var textStyleId = StationTextStyleHelper.Ensure(tr, modelSpace.Database, StationFontPreferences.FontFileName);

        var leftNormal = GetSideNormal(direction.Value, 1.0);
        var labelSideNormal = GetSideNormal(direction.Value, options.LabelSideSign);
        var onCurve = IsStationWithinArc(axis, station);
        var markColor = onCurve
            ? ToAciColor(options.SegmentLabelColorIndex)
            : ToAciColor(options.StationTickColorIndex);
        var textColor = onCurve
            ? ToAciColor(options.SegmentLabelColorIndex)
            : ToAciColor(options.StationTextColorIndex);

        var tickStart = point.Value - leftNormal * rightWidth;
        var tickEnd = point.Value + leftNormal * leftWidth;
        var textTickEnd = options.LabelSideSign >= 0 ? tickEnd : tickStart;

        var tick = new Line(tickStart, tickEnd)
        {
            Layer = StationLayerName,
            Color = markColor
        };
        modelSpace.AppendEntity(tick);
        tr.AddNewlyCreatedDBObject(tick, true);
        CrossAxisXData.AttachStationTickWithCrossAxis(tick, axisName, station, crossAxisNumber);

        var textRotation = GetPerpendicularLabelRotation(direction.Value, options.LabelSideSign);
        var relativeStation = Math.Max(0, station - options.StartStation);
        var lineSpacing = options.TextHeight * 1.35;
        var roadDir = direction.Value.Length > 1e-9 ? direction.Value.GetNormal() : Vector3d.XAxis;
        var basePos = textTickEnd + labelSideNormal * (StationTextGapFromTick + options.TextHeight * 0.25);
        var namePosition = basePos - roadDir * (lineSpacing * 0.5);
        var chainagePosition = basePos + roadDir * (lineSpacing * 0.5);
        var chainagePart = FormatChainage(relativeStation, options.ChainageFormat);

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
        RoadXData.AttachStationLabel(nameText, axisName, RoadXData.RoleText, station);

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
        RoadXData.AttachStationLabel(chainageText, axisName, RoadXData.RoleChainage, station);

        return tick;
    }

    /// <summary>
    /// Oznake za ručno dodatu poprečnu osu — isti sloj, font, boja i položaj kao intervalne stacionaže.
    /// </summary>
    internal static void CreateManualCrossAxisStationLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        StationLabelOptions options,
        string axisName,
        double station,
        double leftWidth,
        double rightWidth,
        string namePart)
    {
        var point = axis.GetPointAtStation(station);
        var direction = axis.SampleDirectionAtStation(station);
        if (point is null || direction is null)
        {
            return;
        }

        EnsureLayer(tr, modelSpace.Database, StationLayerName, Color.FromColorIndex(ColorMethod.ByAci, 3));
        EnsureRegApp(tr, modelSpace.Database);
        StationFontPreferences.Load();
        var textStyleId = StationTextStyleHelper.Ensure(tr, modelSpace.Database, StationFontPreferences.FontFileName);

        var leftNormal = GetSideNormal(direction.Value, 1.0);
        var labelSideNormal = GetSideNormal(direction.Value, options.LabelSideSign);
        var onCurve = IsStationWithinArc(axis, station);
        var textColor = onCurve
            ? ToAciColor(options.SegmentLabelColorIndex)
            : ToAciColor(options.StationTextColorIndex);

        var textTickEnd = options.LabelSideSign >= 0
            ? point.Value + leftNormal * leftWidth
            : point.Value - leftNormal * rightWidth;
        var textRotation = GetPerpendicularLabelRotation(direction.Value, options.LabelSideSign);
        var relativeStation = Math.Max(0, station - options.StartStation);
        var lineSpacing = options.TextHeight * 1.35;
        var roadDir = direction.Value.Length > 1e-9 ? direction.Value.GetNormal() : Vector3d.XAxis;
        var basePos = textTickEnd + labelSideNormal * (StationTextGapFromTick + options.TextHeight * 0.25);
        var namePosition = basePos - roadDir * (lineSpacing * 0.5);
        var chainagePosition = basePos + roadDir * (lineSpacing * 0.5);
        var chainagePart = FormatChainage(relativeStation, options.ChainageFormat);

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
        RoadXData.AttachStationLabel(nameText, axisName, RoadXData.RoleText, station);

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
        RoadXData.AttachStationLabel(chainageText, axisName, RoadXData.RoleChainage, station);
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
        var geometricEnd = axis.Elements[^1].EndStation;
        var end = geometricEnd;
        if (options.EndStation > start + 1e-6)
        {
            // Ne skraćuj ispod stvarne geometrije (npr. posle izmene R).
            end = Math.Max(options.EndStation, geometricEnd);
        }

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

        // Uvek ubaci spoljne granice cele krivine (luk ili S-A-S).
        var curveBoundaries = GetCurveBoundaryStations(axis);
        foreach (var boundary in curveBoundaries)
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
        PruneAutoStationSpacing(result, curveBoundaries, start, end);
        return result;
    }

    private static HashSet<double> GetCurveBoundaryStations(RoadAxis axis)
    {
        var boundaries = new HashSet<double>();
        for (var index = 0; index < axis.Elements.Count; index++)
        {
            var element = axis.Elements[index];
            if (element.Type != AlignmentElementType.Arc || element.Radius < 1e-6)
            {
                continue;
            }

            var first = index;
            var last = index;
            if (first > 0 && axis.Elements[first - 1].Type == AlignmentElementType.Spiral)
            {
                first--;
            }

            if (last + 1 < axis.Elements.Count &&
                axis.Elements[last + 1].Type == AlignmentElementType.Spiral)
            {
                last++;
            }

            boundaries.Add(RoundStation(axis.Elements[first].StartStation));
            boundaries.Add(RoundStation(axis.Elements[last].EndStation));
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

    private const double MinAutoCrossAxisSpacingMeters = 10.0;

    /// <summary>
    /// Automatske poprečne ose moraju biti udaljene najmanje 10 m.
    /// Granice krivina i krajevi ose imaju prioritet; kada su i one preblizu,
    /// zadržava se prva po stacionaži. Ručne CAXIS ose ne prolaze kroz ovaj metod.
    /// </summary>
    private static void PruneAutoStationSpacing(
        List<double> stations,
        IReadOnlyCollection<double> curveBoundaries,
        double start,
        double end)
    {
        const double tolerance = 1e-3;
        if (stations.Count < 2)
        {
            return;
        }

        var protectedStations = new HashSet<double>(curveBoundaries)
        {
            RoundStation(start),
            RoundStation(end)
        };

        var retained = new List<double>();
        foreach (var station in stations.Where(s => IsNearStation(protectedStations, s, tolerance)))
        {
            if (retained.Count == 0 ||
                station - retained[^1] >= MinAutoCrossAxisSpacingMeters - tolerance)
            {
                retained.Add(station);
            }
        }

        foreach (var station in stations.Where(s => !IsNearStation(protectedStations, s, tolerance)))
        {
            if (retained.All(kept =>
                    Math.Abs(kept - station) >= MinAutoCrossAxisSpacingMeters - tolerance))
            {
                retained.Add(station);
            }
        }

        retained.Sort();
        stations.Clear();
        stations.AddRange(retained);
    }

    private static double RoundStation(double station) => Math.Round(station, 6);

    /// <summary>
    /// Plateia stil: oznake na početku i kraju svake krivine.
    /// DimStyle "TCM-Radijus" označava namenu kotiranja radijusa.
    /// </summary>
    public static int DrawRadiusLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        double textHeight,
        double labelSideSign = DefaultLabelSideSign)
    {
        var height = Math.Max(0.1, textHeight);
        EnsureLayer(
            tr,
            modelSpace.Database,
            RadiusLayerName,
            Color.FromColorIndex(ColorMethod.ByAci, 1));
        EnsureRadiusLayerColor(tr, modelSpace.Database);
        EnsureRadiusDimStyle(tr, modelSpace.Database, height);
        EnsureRegApp(tr, modelSpace.Database);

        var count = 0;
        var leaderLength = height * 2.0;
        for (var index = 0; index < axis.Elements.Count; index++)
        {
            var element = axis.Elements[index];
            if (element.Type != AlignmentElementType.Arc || element.Radius <= 1e-6)
            {
                continue;
            }

            var first = index;
            var last = index;
            AlignmentElement? entrySpiral = null;
            AlignmentElement? exitSpiral = null;
            if (first > 0 && axis.Elements[first - 1].Type == AlignmentElementType.Spiral)
            {
                entrySpiral = axis.Elements[--first];
            }

            if (last + 1 < axis.Elements.Count &&
                axis.Elements[last + 1].Type == AlignmentElementType.Spiral)
            {
                exitSpiral = axis.Elements[++last];
            }

            var groupLongEnough =
                axis.Elements[last].EndStation - axis.Elements[first].StartStation >=
                MinAutoCrossAxisSpacingMeters - 1e-3;

            // TS / ST: parametri prelaznice A= i L= (crveno).
            if (entrySpiral is not null)
            {
                count += AppendSpiralAnnotation(
                    tr, modelSpace, axis, index, entrySpiral.StartStation,
                    entrySpiral, labelSideSign, leaderLength, height);
            }

            if (exitSpiral is not null && groupLongEnough)
            {
                count += AppendSpiralAnnotation(
                    tr, modelSpace, axis, index, exitSpiral.EndStation,
                    exitSpiral, labelSideSign, leaderLength, height);
            }

            // SC / CS (odnosno BC / EC bez prelaznica): radijus luka.
            count += AppendStationAnnotation(
                tr, modelSpace, axis, index, element.StartStation,
                FormatRadius(element.Radius), element.Radius,
                null, labelSideSign, leaderLength, height);

            if (element.EndStation - element.StartStation >=
                MinAutoCrossAxisSpacingMeters - 1e-3)
            {
                count += AppendStationAnnotation(
                    tr, modelSpace, axis, index, element.EndStation,
                    FormatRadius(element.Radius), element.Radius,
                    null, labelSideSign, leaderLength, height);
            }
        }

        return count;
    }

    private static int AppendSpiralAnnotation(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        int elementIndex,
        double station,
        AlignmentElement spiral,
        double labelSideSign,
        double leaderLength,
        double textHeight)
    {
        var a = spiral.SpiralA > 1e-9
            ? spiral.SpiralA
            : Math.Sqrt(Math.Max(0, spiral.Radius * spiral.Length));
        return AppendStationAnnotation(
            tr, modelSpace, axis, elementIndex, station,
            FormatSpiralA(a), a, $"L={spiral.Length:F2}",
            labelSideSign, leaderLength, textHeight);
    }

    private static int AppendStationAnnotation(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        int elementIndex,
        double station,
        string label,
        double value,
        string? secondaryLabel,
        double labelSideSign,
        double leaderLength,
        double textHeight)
    {
        var point = axis.GetPointAtStation(station);
        var direction = axis.GetDirectionAtStation(station) ??
                        axis.SampleDirectionAtStation(station);
        if (point is null || direction is null || direction.Value.Length < 1e-9)
        {
            return 0;
        }

        return AppendPerpendicularRadiusAnnotation(
            tr, modelSpace, axis.Name, elementIndex, point.Value, direction.Value,
            labelSideSign, leaderLength, textHeight, label, value, secondaryLabel);
    }

    private static int AppendPerpendicularRadiusAnnotation(
        Transaction tr,
        BlockTableRecord modelSpace,
        string axisName,
        int elementIndex,
        Point3d axisPoint,
        Vector3d direction,
        double labelSideSign,
        double leaderLength,
        double textHeight,
        string label,
        double value,
        string? secondaryLabel = null)
    {
        var normal = GetSideNormal(direction, labelSideSign);
        var tip = axisPoint + normal * leaderLength;
        var red = Color.FromColorIndex(ColorMethod.ByAci, 1);

        var leader = new Line(axisPoint, tip)
        {
            Layer = RadiusLayerName,
            Color = red
        };
        modelSpace.AppendEntity(leader);
        tr.AddNewlyCreatedDBObject(leader, true);
        RoadXData.AttachRadiusAnnotation(
            leader, axisName, RoadXData.RoleRadiusDimArc, elementIndex, value);

        var arrowLength = textHeight * 0.35;
        var along = direction.GetNormal() * arrowLength;
        var arrowBase = axisPoint + normal * arrowLength;
        foreach (var arrowEnd in new[] { arrowBase + along, arrowBase - along })
        {
            var arrow = new Line(axisPoint, arrowEnd)
            {
                Layer = RadiusLayerName,
                Color = red
            };
            modelSpace.AppendEntity(arrow);
            tr.AddNewlyCreatedDBObject(arrow, true);
            RoadXData.AttachRadiusAnnotation(
                arrow, axisName, RoadXData.RoleRadiusDimArc, elementIndex, value);
        }

        var rotation = Math.Atan2(normal.Y, normal.X);
        // Tekst čitljiv: ako je naopako, rotiraj za 180°.
        if (Math.Cos(rotation) < 0)
        {
            rotation += Math.PI;
        }

        var textAnchor = tip + normal * (textHeight * 0.35);
        var text = new DBText
        {
            Position = GetHorizontallyCenteredPosition(textAnchor, label, textHeight, rotation),
            Height = textHeight,
            TextString = label,
            Layer = RadiusLayerName,
            Color = red,
            Rotation = rotation
        };
        modelSpace.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
        RoadXData.AttachRadiusAnnotation(
            text, axisName, RoadXData.RoleRadiusText, elementIndex, value);

        var count = 4;
        if (!string.IsNullOrWhiteSpace(secondaryLabel))
        {
            var secondaryAnchor = textAnchor + direction.GetNormal() * (textHeight * 1.35);
            var secondary = new DBText
            {
                Position = GetHorizontallyCenteredPosition(
                    secondaryAnchor, secondaryLabel, textHeight, rotation),
                Height = textHeight,
                TextString = secondaryLabel,
                Layer = RadiusLayerName,
                Color = red,
                Rotation = rotation
            };
            modelSpace.AppendEntity(secondary);
            tr.AddNewlyCreatedDBObject(secondary, true);
            RoadXData.AttachRadiusAnnotation(
                secondary, axisName, RoadXData.RoleRadiusText, elementIndex, value);
            count++;
        }

        return count;
    }

    /// <summary>DimStyle za kotiranje radijusa — crvena, bez dominantnih strelica.</summary>
    internal static void EnsureRadiusDimStyle(Transaction tr, Database db, double textHeight)
    {
        var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        var height = Math.Max(0.1, textHeight);
        var red = Color.FromColorIndex(ColorMethod.ByAci, 1);
        if (table.Has(RadiusDimStyleName))
        {
            var existing = (DimStyleTableRecord)tr.GetObject(
                table[RadiusDimStyleName], OpenMode.ForWrite);
            existing.Dimclrd = red;
            existing.Dimclre = red;
            existing.Dimclrt = red;
            existing.Dimtxt = height;
            existing.Dimasz = height * 0.05;
            existing.Dimexe = 0;
            existing.Dimexo = 0;
            existing.Dimdle = 0;
            return;
        }

        table.UpgradeOpen();
        var style = new DimStyleTableRecord
        {
            Name = RadiusDimStyleName,
            Dimclrd = red,
            Dimclre = red,
            Dimclrt = red,
            Dimtxt = height,
            Dimasz = height * 0.05,
            Dimexe = 0,
            Dimexo = 0,
            Dimdle = 0
        };
        table.Add(style);
        tr.AddNewlyCreatedDBObject(style, true);
    }

    private static void EnsureRadiusLayerColor(Transaction tr, Database db)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layerTable.Has(RadiusLayerName))
        {
            return;
        }

        var layer = (LayerTableRecord)tr.GetObject(layerTable[RadiusLayerName], OpenMode.ForWrite);
        if (layer.Color.ColorIndex != 1)
        {
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
        }
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

    /// <summary>
    /// Na svakom preseku tangenti (čvor T1, T2…) crta marker, oznaku i dinamičku tabelu parametara.
    /// </summary>
    public static int DrawTangentNodeTables(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        double textHeight)
    {
        EnsureLayer(tr, modelSpace.Database, TangentNodeLayerName, Color.FromColorIndex(ColorMethod.ByAci, 7));
        EnsureRegApp(tr, modelSpace.Database);

        var nodes = TangentNodeGeometry.Collect(axis);
        if (nodes.Count == 0)
        {
            return 0;
        }

        var height = Math.Max(0.5, textHeight);
        var count = 0;
        foreach (var node in nodes)
        {
            count += DrawSingleTangentNode(tr, modelSpace, axis.Name, node, height);
        }

        return count;
    }

    private static int DrawSingleTangentNode(
        Transaction tr,
        BlockTableRecord modelSpace,
        string axisName,
        TangentNodeInfo node,
        double textHeight)
    {
        var count = 0;
        var yellow = Color.FromColorIndex(ColorMethod.ByAci, 2);
        var blue = Color.FromColorIndex(ColorMethod.ByAci, 5);

        var bisector = node.OpenBisector;
        if (bisector.Length < 1e-9)
        {
            bisector = Vector3d.YAxis;
        }
        else
        {
            bisector = bisector.GetNormal();
        }

        // Lokalna osa tabele: "desno" = čitljiv smer teksta, "gore" = smer narednog reda (CCW od desno).
        var right = new Vector3d(-bisector.Y, bisector.X, 0).GetNormal();
        if (Math.Cos(Math.Atan2(right.Y, right.X)) < 0)
        {
            right = right.Negate();
        }

        var textUp = new Vector3d(-right.Y, right.X, 0).GetNormal();
        var textRotation = Math.Atan2(right.Y, right.X);

        // Žuti marker na PI.
        var markerRadius = textHeight * 0.35;
        var circle = new Circle(node.Pi, Vector3d.ZAxis, markerRadius)
        {
            Layer = TangentNodeLayerName,
            Color = yellow
        };
        modelSpace.AppendEntity(circle);
        tr.AddNewlyCreatedDBObject(circle, true);
        RoadXData.AttachTangentNode(circle, axisName, node.Number);
        count++;

        // Oznaka T1, T2… pored čvora (ne rotirana).
        var label = new DBText
        {
            Position = node.Pi + bisector * (textHeight * 0.9) + right * (textHeight * 1.2),
            Height = textHeight * 1.4,
            TextString = $"T{node.Number}",
            Layer = TangentNodeLayerName,
            Color = blue,
            Rotation = 0
        };
        modelSpace.AppendEntity(label);
        tr.AddNewlyCreatedDBObject(label, true);
        RoadXData.AttachTangentNode(label, axisName, node.Number);
        count++;

        var lines = BuildTangentNodeTableLines(node);
        var lineHeight = textHeight * 1.15;
        var padding = textHeight * 0.45;
        var maxWidth = 0.0;
        foreach (var line in lines)
        {
            maxWidth = Math.Max(maxWidth, EstimateTextWidth(line, textHeight));
        }

        var boxWidth = maxWidth + padding * 2;
        var boxHeight = lines.Count * lineHeight + padding * 2;
        var halfW = boxWidth / 2.0;
        var halfH = boxHeight / 2.0;

        // Rotirana tabela: bliža ivica na odmaku 25 duž bisektrise (ka čvoru).
        var boxCenter = node.Pi + bisector * (DefaultTangentNodeTableOffset + halfH);
        var nearLeft = boxCenter - right * halfW - bisector * halfH;
        var nearRight = boxCenter + right * halfW - bisector * halfH;
        var farRight = boxCenter + right * halfW + bisector * halfH;
        var farLeft = boxCenter - right * halfW + bisector * halfH;

        var frame = new Polyline();
        frame.AddVertexAt(0, new Point2d(nearLeft.X, nearLeft.Y), 0, 0, 0);
        frame.AddVertexAt(1, new Point2d(nearRight.X, nearRight.Y), 0, 0, 0);
        frame.AddVertexAt(2, new Point2d(farRight.X, farRight.Y), 0, 0, 0);
        frame.AddVertexAt(3, new Point2d(farLeft.X, farLeft.Y), 0, 0, 0);
        frame.Closed = true;
        frame.Layer = TangentNodeLayerName;
        frame.Color = Color.FromColorIndex(ColorMethod.ByAci, 7);
        modelSpace.AppendEntity(frame);
        tr.AddNewlyCreatedDBObject(frame, true);
        RoadXData.AttachTangentNode(frame, axisName, node.Number);
        count++;

        var leaderAnchor = node.Pi + bisector * DefaultTangentNodeTableOffset;
        var leader = new Line(node.Pi, leaderAnchor)
        {
            Layer = TangentNodeLayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
        };
        modelSpace.AppendEntity(leader);
        tr.AddNewlyCreatedDBObject(leader, true);
        RoadXData.AttachTangentNode(leader, axisName, node.Number);
        count++;

        for (var i = 0; i < lines.Count; i++)
        {
            // Prvi red (T=) uvek na "vrhu" teksta u smeru čitanja (textUp), ne nužno dalje od čvora.
            var localY = halfH - padding - (i + 0.75) * lineHeight;
            var localX = -halfW + padding;
            var textPos = boxCenter + right * localX + textUp * localY;
            var cell = new DBText
            {
                Position = textPos,
                Height = textHeight,
                TextString = lines[i],
                Layer = TangentNodeLayerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 7),
                Rotation = textRotation
            };
            modelSpace.AppendEntity(cell);
            tr.AddNewlyCreatedDBObject(cell, true);
            RoadXData.AttachTangentNode(cell, axisName, node.Number);
            count++;
        }

        return count;
    }

    private static IReadOnlyList<string> BuildTangentNodeTableLines(TangentNodeInfo node)
    {
        var lines = new List<string>
        {
            $"T= {node.Number}",
            $"x= {node.Pi.X:0.000}",
            $"y= {node.Pi.Y:0.000}",
            $"α= {FormatDegreesMinutesSeconds(node.DeflectionRadians)}",
            $"R= {node.Radius:0.000}"
        };

        if (node.L1 > 1e-6)
        {
            lines.Add($"L1= {node.L1:0.000}");
        }

        if (node.L2 > 1e-6)
        {
            lines.Add($"L2= {node.L2:0.000}");
        }

        lines.Add($"dl= {node.ArcLength:0.000}");
        lines.Add($"dk= {node.ArcLength:0.000}");
        lines.Add($"T1= {node.TangentLength1:0.000}");
        lines.Add($"T2= {node.TangentLength2:0.000}");
        lines.Add($"b= {node.ExternalDistance:0.000}");
        return lines;
    }

    private static string FormatDegreesMinutesSeconds(double radians)
    {
        var degrees = Math.Abs(radians) * 180.0 / Math.PI;
        var d = (int)Math.Floor(degrees);
        var minutesFull = (degrees - d) * 60.0;
        var m = (int)Math.Floor(minutesFull);
        var s = (minutesFull - m) * 60.0;
        if (s >= 59.95)
        {
            s = 0;
            m++;
        }

        if (m >= 60)
        {
            m = 0;
            d++;
        }

        return $"{d}°{m}'{s:0.0}\"";
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
        Database db) =>
        CreateStationDbTextPublic(contents, alignmentPoint, height, color, rotation, textStyleId, db);

    internal static DBText CreateStationDbTextPublic(
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

    private static Polyline CreateSpiralPolyline(AlignmentElement element)
    {
        var pts = element.SpiralPoints;
        if (pts is null || pts.Count < 2)
        {
            pts = new[] { element.Start, element.End };
        }

        var pl = new Polyline(pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, 0, 0);
        }

        pl.Elevation = pts[0].Z;
        return pl;
    }

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
        EnsureLayer(tr, db, layerName, color, linetypeName: null);
    }

    private static void EnsureLayer(
        Transaction tr,
        Database db,
        string layerName,
        Color color,
        string? linetypeName)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(layerName))
        {
            if (string.IsNullOrWhiteSpace(linetypeName))
            {
                return;
            }

            var existing = (LayerTableRecord)tr.GetObject(layerTable[layerName], OpenMode.ForWrite);
            if (TryGetLinetypeId(tr, db, linetypeName, out var existingLtId) &&
                existing.LinetypeObjectId != existingLtId)
            {
                existing.LinetypeObjectId = existingLtId;
            }

            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = layerName,
            Color = color
        };
        if (!string.IsNullOrWhiteSpace(linetypeName) &&
            TryGetLinetypeId(tr, db, linetypeName, out var ltId))
        {
            layer.LinetypeObjectId = ltId;
        }

        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>
    /// Izvorna polilinija (tangentni poligon): sloj TCM_TANG_POLIGON + isprekidana (DASHED) linija.
    /// </summary>
    public static void StyleSourcePolyline(Transaction tr, Database db, Entity polyline)
    {
        EnsureDashedLinetype(tr, db);
        EnsureLayer(
            tr,
            db,
            SourcePolylineLayerName,
            Color.FromColorIndex(ColorMethod.ByAci, 8),
            DashedLinetypeName);

        if (!polyline.IsWriteEnabled)
        {
            polyline.UpgradeOpen();
        }

        polyline.Layer = SourcePolylineLayerName;
        if (TryGetLinetypeId(tr, db, DashedLinetypeName, out _))
        {
            polyline.Linetype = DashedLinetypeName;
            polyline.LinetypeScale = 10.0;
        }

        // Tangenta uvek iznad ose / 3D projekcije radi pick-a i pomeranja.
        BringEntityToFront(tr, db, polyline.ObjectId);
    }

    /// <summary>
    /// Izvorna tangenta (SRCPL) ide na vrh draw order-a — klik bira nju, ne osovinu/3D poly.
    /// </summary>
    public static void EnsureTangentOnTop(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null ||
            !metadata.HasSourcePolyline ||
            !AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out var polylineId))
        {
            return;
        }

        BringEntityToFront(tr, db, polylineId);
    }

    public static void BringEntityToFront(Transaction tr, Database db, ObjectId entityId)
    {
        if (entityId.IsNull || entityId.IsErased)
        {
            return;
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);
        if (modelSpace.DrawOrderTableId.IsNull)
        {
            return;
        }

        try
        {
            var drawOrder = (DrawOrderTable)tr.GetObject(modelSpace.DrawOrderTableId, OpenMode.ForWrite);
            drawOrder.MoveToTop(new ObjectIdCollection { entityId });
        }
        catch
        {
            // Draw order nije kritičan ako objekat nije u model space.
        }
    }

    /// <summary>
    /// Crta 3D poliliniju — projekciju ose na teren (XY na plan-osi, Z sa terena).
    /// Sloj ostaje zaključan da se kroz njega bira / pomera izvorna tangenta.
    /// </summary>
    public static ObjectId DrawProjectedAxis(
        Transaction tr,
        BlockTableRecord modelSpace,
        string axisName,
        IReadOnlyList<Point3d> points)
    {
        EnsureRegApp(tr, modelSpace.Database);
        EnsureProjectedAxisLayer(tr, modelSpace.Database);
        UnlockProjectedAxisLayer(tr, modelSpace.Database);

        if (points.Count < 2)
        {
            LockProjectedAxisLayer(tr, modelSpace.Database);
            return ObjectId.Null;
        }

        ObjectId id;
        try
        {
            var poly = new Polyline3d();
            poly.SetDatabaseDefaults(modelSpace.Database);
            poly.Layer = ProjectedAxisLayerName;
            poly.Color = Color.FromColorIndex(ColorMethod.ByAci, 6);
            modelSpace.AppendEntity(poly);
            tr.AddNewlyCreatedDBObject(poly, true);

            foreach (var point in points)
            {
                var vertex = new PolylineVertex3d(point);
                poly.AppendVertex(vertex);
                tr.AddNewlyCreatedDBObject(vertex, true);
            }

            RoadXData.AttachProjectedAxis(poly, axisName);
            id = poly.ObjectId;
        }
        finally
        {
            LockProjectedAxisLayer(tr, modelSpace.Database);
        }

        return id;
    }

    public static void EnsureProjectedAxisLayerPickThrough(Transaction tr, Database db)
    {
        EnsureProjectedAxisLayer(tr, db);
        LockProjectedAxisLayer(tr, db);
    }

    public static void RunWithUnlockedProjectedAxisLayer(Transaction tr, Database db, Action action)
    {
        EnsureProjectedAxisLayer(tr, db);
        UnlockProjectedAxisLayer(tr, db);
        try
        {
            action();
        }
        finally
        {
            LockProjectedAxisLayer(tr, db);
        }
    }

    /// <summary>
    /// Posle crtanja 3D projekcije: tangenta ostaje na vrhu draw order-a.
    /// </summary>
    public static void SendProjectedAxisBelowPickables(
        Transaction tr,
        Database db,
        ObjectId projectedId,
        string axisName)
    {
        if (!projectedId.IsNull && !projectedId.IsErased)
        {
            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForRead);
            if (!modelSpace.DrawOrderTableId.IsNull)
            {
                try
                {
                    var drawOrder = (DrawOrderTable)tr.GetObject(modelSpace.DrawOrderTableId, OpenMode.ForWrite);
                    drawOrder.MoveToBottom(new ObjectIdCollection { projectedId });
                }
                catch
                {
                    // Ignoriši.
                }
            }
        }

        EnsureTangentOnTop(tr, db, axisName);
    }

    private static void EnsureProjectedAxisLayer(Transaction tr, Database db)
    {
        EnsureLayer(tr, db, ProjectedAxisLayerName, Color.FromColorIndex(ColorMethod.ByAci, 6));
    }

    private static void UnlockProjectedAxisLayer(Transaction tr, Database db)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layerTable.Has(ProjectedAxisLayerName))
        {
            return;
        }

        var layer = (LayerTableRecord)tr.GetObject(layerTable[ProjectedAxisLayerName], OpenMode.ForWrite);
        layer.IsLocked = false;
    }

    private static void LockProjectedAxisLayer(Transaction tr, Database db)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layerTable.Has(ProjectedAxisLayerName))
        {
            return;
        }

        var layer = (LayerTableRecord)tr.GetObject(layerTable[ProjectedAxisLayerName], OpenMode.ForWrite);
        layer.IsLocked = true;
    }

    private static void EnsureDashedLinetype(Transaction tr, Database db)
    {
        var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(DashedLinetypeName))
        {
            return;
        }

        foreach (var file in new[] { "acad.lin", "acadiso.lin" })
        {
            try
            {
                db.LoadLineTypeFile(DashedLinetypeName, file);
                table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (table.Has(DashedLinetypeName))
                {
                    return;
                }
            }
            catch
            {
                // Probaj sledeći .lin fajl.
            }
        }
    }

    private static bool TryGetLinetypeId(Transaction tr, Database db, string linetypeName, out ObjectId linetypeId)
    {
        linetypeId = ObjectId.Null;
        EnsureDashedLinetype(tr, db);
        var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (!table.Has(linetypeName))
        {
            return false;
        }

        linetypeId = table[linetypeName];
        return true;
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

    public static string FormatSpiralA(double spiralA) => $"A={spiralA:F2}";

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
}
