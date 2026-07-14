using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal static class CrossAxisLayoutService
{
    public static int ApplyPlacement(
        Transaction tr,
        Database db,
        IEnumerable<long> handles,
        CrossAxisPlacementSettings settings)
    {
        var usedNumbers = CrossAxisScanner.Scan(tr, db)
            .Select(axis => axis.Number)
            .ToHashSet();

        var count = 0;
        foreach (var handle in handles)
        {
            if (!CrossAxisScanner.TryGetEntity(tr, db, handle, out var axisEntity))
            {
                continue;
            }

            CrossAxisAnnotationBinder.TrySyncAxisNumberFromNearbyLabel(
                tr,
                db,
                axisEntity,
                usedNumbers,
                out _);

            CrossAxisStore.Save(tr, db, handle, settings);
            count += RepositionAnnotations(tr, db, axisEntity, settings);
        }

        return count;
    }

    /// <summary>
    /// Posle pomeranja putne ose: premesti poprečne ose na nove STA tačke
    /// i ponovo primeni sačuvane odmake (bez "duhova" na staroj lokaciji).
    /// </summary>
    public static int SyncToRoadAxis(
        Transaction tr,
        Database db,
        string roadAxisName,
        RoadAxis roadAxis,
        StationLabelOptions options)
    {
        if (roadAxis.Elements.Count == 0)
        {
            return 0;
        }

        var stations = RoadDrawing.CollectStationsForSync(roadAxis, options);
        if (stations.Count == 0)
        {
            return 0;
        }

        var handles = CrossAxisScanner.Scan(tr, db)
            .Select(axis => axis.Handle)
            .ToList();

        var updated = 0;
        foreach (var handle in handles)
        {
            if (!CrossAxisScanner.TryGetEntity(tr, db, handle, out var axisEntity))
            {
                continue;
            }

            if (!CrossAxisXData.TryReadCrossAxis(axisEntity, out var number, out var parent))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(parent) &&
                !string.Equals(parent, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stationIndex = number - options.AxisCounterStart;
            if (stationIndex < 0 || stationIndex >= stations.Count)
            {
                // Ako broj ne odgovara, obriši "tick-like" poprečnu osu ostavljenu na staroj lokaciji.
                if (IsTickLikeCrossAxis(axisEntity, options.TickLength))
                {
                    if (!axisEntity.IsWriteEnabled)
                    {
                        axisEntity.UpgradeOpen();
                    }

                    axisEntity.Erase();
                }

                continue;
            }

            var station = stations[stationIndex];
            var point = roadAxis.GetPointAtStation(station);
            var direction = roadAxis.SampleDirectionAtStation(station);
            if (point is null || direction is null)
            {
                continue;
            }

            var length = GetEntityLength(axisEntity);
            if (length < 1e-6)
            {
                length = Math.Max(options.TickLength, 1.0);
            }

            var across = new Vector3d(-direction.Value.Y, direction.Value.X, 0);
            if (across.Length < 1e-9)
            {
                continue;
            }

            across = across.GetNormal();
            var half = length * 0.5;
            var start = point.Value - across * half;
            var end = point.Value + across * half;

            if (!axisEntity.IsWriteEnabled)
            {
                axisEntity.UpgradeOpen();
            }

            switch (axisEntity)
            {
                case Line line:
                    line.StartPoint = start;
                    line.EndPoint = end;
                    break;
                case Polyline polyline:
                    while (polyline.NumberOfVertices > 0)
                    {
                        polyline.RemoveVertexAt(0);
                    }

                    polyline.AddVertexAt(0, new Point2d(start.X, start.Y), 0, 0, 0);
                    polyline.AddVertexAt(1, new Point2d(end.X, end.Y), 0, 0, 0);
                    break;
                default:
                    continue;
            }

            CrossAxisXData.AttachCrossAxis(axisEntity, number, roadAxisName);
            var settings = CrossAxisStore.Load(tr, db, handle);
            updated += RepositionAnnotations(
                tr,
                db,
                axisEntity,
                settings,
                options,
                station,
                roadAxisName);
        }

        return updated;
    }

    private static bool IsTickLikeCrossAxis(Entity entity, double tickLength)
    {
        var length = GetEntityLength(entity);
        var limit = Math.Max(tickLength * 3.0, 40.0);
        return length > 1e-6 && length <= limit;
    }

    private static double GetEntityLength(Entity entity) =>
        entity switch
        {
            Line line => line.Length,
            Polyline polyline => polyline.Length,
            _ => 0
        };

    public static int RegisterEntities(Transaction tr, Database db, IEnumerable<ObjectId> entityIds)
    {
        RoadDrawing.EnsureRegApp(tr, db);
        EnsureLayer(tr, db);

        var usedNumbers = CrossAxisScanner.Scan(tr, db)
            .Select(axis => axis.Number)
            .ToHashSet();
        var nextNumber = usedNumbers.Count == 0 ? 1 : usedNumbers.Max() + 1;
        var count = 0;

        foreach (var id in entityIds)
        {
            if (id.IsNull)
            {
                continue;
            }

            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            if (entity is not Line and not Polyline)
            {
                continue;
            }

            // Crvenu osovinu (line/arc element) nikad ne pretvaraj u poprečnu.
            if (RoadXData.TryReadAxisElement(entity, out _, out _))
            {
                continue;
            }

            // Tekst stacionaze ne moze biti osa.
            if (RoadXData.TryReadStationLabel(entity, out _, out var stationRole, out _) &&
                (stationRole == RoadXData.RoleText || stationRole == RoadXData.RoleChainage))
            {
                continue;
            }

            if (CrossAxisXData.TryReadCrossAxis(entity, out var existingNumber))
            {
                CrossAxisAnnotationBinder.TrySyncAxisNumberFromNearbyLabel(
                    tr,
                    db,
                    entity,
                    usedNumbers,
                    out existingNumber);

                if (CrossAxisGeometry.TryGetFrame(entity, out var existingOrigin, out _, out _))
                {
                    CrossAxisXData.TryReadCrossAxis(entity, out _, out var existingParent);
                    CrossAxisAnnotationBinder.FindOrBindAnnotations(
                        tr,
                        db,
                        existingNumber,
                        existingOrigin,
                        existingParent);
                }

                continue;
            }

            string? parentRoadAxis = null;
            int? preferredNumber = null;

            // Dozvoli izbor štapića stacionaze (RoleTick) kao poprečne ose.
            if (RoadXData.TryReadStationLabel(entity, out var tickAxisName, out var tickRole, out var tickStation) &&
                tickRole == RoadXData.RoleTick)
            {
                parentRoadAxis = tickAxisName;
                preferredNumber = ResolveStaNumberFromRoadStation(tr, db, tickAxisName, tickStation);
            }

            if (CrossAxisGeometry.TryGetFrame(entity, out var origin, out _, out _))
            {
                parentRoadAxis ??= FindNearbyRoadAxisName(tr, db, entity);
                preferredNumber ??= CrossAxisAnnotationBinder.FindNearestStaNumber(tr, db, origin);
            }

            var number = preferredNumber ?? nextNumber;
            while (usedNumbers.Contains(number))
            {
                number++;
            }

            entity.Layer = CrossAxisScanner.LayerName;
            CrossAxisXData.AttachCrossAxis(entity, number, parentRoadAxis);
            CrossAxisStore.Save(tr, db, entity.Handle.Value, new CrossAxisPlacementSettings());
            usedNumbers.Add(number);
            nextNumber = Math.Max(nextNumber, number + 1);

            if (CrossAxisGeometry.TryGetFrame(entity, out origin, out _, out _))
            {
                CrossAxisAnnotationBinder.FindOrBindAnnotations(tr, db, number, origin, parentRoadAxis);
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// Registruje sve štapiće stacionaze (RoleTick) kao poprečne ose — bez menjanja sloja —
    /// da TCMPOPOSPOZ tabela bude popunjena i sve selektovano po defaultu.
    /// </summary>
    public static int EnsureStationTicksRegistered(Transaction tr, Database db)
    {
        RoadDrawing.EnsureRegApp(tr, db);

        var usedNumbers = CrossAxisScanner.Scan(tr, db)
            .Select(axis => axis.Number)
            .ToHashSet();
        var nextNumber = usedNumbers.Count == 0 ? 1 : usedNumbers.Max() + 1;
        var count = 0;

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (entity is not Line and not Polyline)
            {
                continue;
            }

            if (CrossAxisXData.TryReadCrossAxis(entity, out _))
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var tickAxisName, out var tickRole, out var tickStation) ||
                tickRole != RoadXData.RoleTick)
            {
                continue;
            }

            if (!entity.IsWriteEnabled)
            {
                entity.UpgradeOpen();
            }

            var preferredNumber = ResolveStaNumberFromRoadStation(tr, db, tickAxisName, tickStation);
            var number = preferredNumber ?? nextNumber;
            while (usedNumbers.Contains(number))
            {
                number++;
            }

            CrossAxisXData.AttachCrossAxis(entity, number, tickAxisName);
            CrossAxisStore.Save(tr, db, entity.Handle.Value, new CrossAxisPlacementSettings());
            usedNumbers.Add(number);
            nextNumber = Math.Max(nextNumber, number + 1);
            count++;
        }

        return count;
    }

    private static int? ResolveStaNumberFromRoadStation(
        Transaction tr,
        Database db,
        string roadAxisName,
        double stationValue)
    {
        var metadata = RoadAxisStore.Load(tr, db, roadAxisName);
        if (metadata is null)
        {
            return null;
        }

        RoadAxis? axis = null;
        if (metadata.HasSourcePolyline &&
            AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out var polylineId))
        {
            var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
            axis = PolylineToTangentConverter.Convert(polyline, metadata.CurveRadius, 0, roadAxisName);
            axis = RoadAxisTrimmer.Trim(axis, metadata.StartStation, metadata.EndStation);
        }
        else
        {
            axis = AxisGeometryReader.ReadAxis(tr, db, roadAxisName, metadata.StartStation);
        }

        if (axis is null || axis.Elements.Count == 0)
        {
            return null;
        }

        var options = metadata.ToLabelOptions();
        var stations = RoadDrawing.CollectStationsForSync(axis, options);
        for (var i = 0; i < stations.Count; i++)
        {
            if (Math.Abs(stations[i] - stationValue) < 1e-3)
            {
                return options.AxisCounterStart + i;
            }
        }

        return null;
    }

    private static string? FindNearbyRoadAxisName(Transaction tr, Database db, Entity axisEntity)
    {
        if (!CrossAxisGeometry.TryGetFrame(axisEntity, out var origin, out _, out _))
        {
            return null;
        }

        string? best = null;
        var bestDistance = double.MaxValue;
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var axisName, out _, out _))
            {
                continue;
            }

            if (!CrossAxisAnnotationBinder.TryGetTextContent(entity, out _, out var position))
            {
                continue;
            }

            var distance = origin.DistanceTo(position);
            if (distance < bestDistance && distance < 50.0)
            {
                bestDistance = distance;
                best = axisName;
            }
        }

        return best;
    }

    public static bool TryComputeOffsetsFromPoint(
        Entity axisEntity,
        Point3d pickedPoint,
        out double offsetX,
        out double offsetY)
    {
        offsetX = 0;
        offsetY = 0;
        if (!CrossAxisGeometry.TryGetFrame(axisEntity, out var origin, out var along, out var across))
        {
            return false;
        }

        var delta = pickedPoint - origin;
        offsetX = delta.DotProduct(along);
        var acrossOffset = delta.DotProduct(across);
        offsetY = acrossOffset;
        return true;
    }

    private static int RepositionAnnotations(
        Transaction tr,
        Database db,
        Entity axisEntity,
        CrossAxisPlacementSettings settings,
        StationLabelOptions? options = null,
        double? stationValue = null,
        string? roadAxisName = null)
    {
        if (!CrossAxisXData.TryReadCrossAxis(axisEntity, out var number, out var parentRoadAxisName))
        {
            return 0;
        }

        if (!CrossAxisGeometry.TryGetFrame(axisEntity, out var origin, out var along, out var across))
        {
            return 0;
        }

        parentRoadAxisName = string.IsNullOrWhiteSpace(parentRoadAxisName)
            ? roadAxisName
            : parentRoadAxisName;

        // Tick stacionaze nosi stvarnu stacionazu — koristi je za "Samo stacionaza".
        if (stationValue is null &&
            RoadXData.TryReadStationLabel(axisEntity, out var tickAxis, out var tickRole, out var tickStation) &&
            tickRole == RoadXData.RoleTick)
        {
            stationValue = tickStation;
            if (string.IsNullOrWhiteSpace(parentRoadAxisName))
            {
                parentRoadAxisName = tickAxis;
            }
        }

        if (options is null &&
            !string.IsNullOrWhiteSpace(parentRoadAxisName) &&
            RoadAxisStore.Load(tr, db, parentRoadAxisName) is { } metadata)
        {
            options = metadata.ToLabelOptions();
            stationValue ??= ResolveStationValueForNumber(tr, db, parentRoadAxisName, number, options);
        }

        // Uvek samo pomeri postojeći RoleText / vec vezane CX oznake.
        // Bez brisanja, bez kreiranja stubova, bez splitovanja — TCMPOPOSPOZ ne sme da brise stacionaze.
        return RepositionExistingStationTexts(
            tr,
            db,
            origin,
            along,
            across,
            settings,
            number,
            parentRoadAxisName,
            stationValue);
    }

    /// <summary>
    /// Pomera RoleText (OSA 20) i RoleChainage (0-380.00) nezavisno.
    /// Ako postoji stari jedinstveni tekst "OSA 20 0-380.00", prvo ga programski razdvoji.
    /// </summary>
    private static int RepositionExistingStationTexts(
        Transaction tr,
        Database db,
        Point3d origin,
        Vector3d along,
        Vector3d across,
        CrossAxisPlacementSettings settings,
        int axisNumber,
        string? parentRoadAxisName,
        double? stationValue)
    {
        var label = FindRoadStationPart(tr, db, parentRoadAxisName, stationValue, origin, RoadXData.RoleText);
        var chainage = FindRoadStationPart(tr, db, parentRoadAxisName, stationValue, origin, RoadXData.RoleChainage);

        // Stari crtezi: jedan RoleText "OSA 20 0-380.00" → razdvoji bez brisanja.
        if (label is not null &&
            chainage is null &&
            CrossAxisAnnotationBinder.TryGetTextContent(tr, label, out var combined, out _) &&
            CrossAxisAnnotationBinder.TryParseCombined(combined, out var prefix, out var parsedNumber, out var chainageText))
        {
            chainage = SplitCombinedRoadLabel(tr, db, label, prefix, parsedNumber, chainageText);
        }

        // Samo stacionaza (jedan tekst, nije kombinovani format).
        if (label is not null && chainage is null)
        {
            CrossAxisOffsetSettings? target = null;
            if (settings.Stations.Enabled)
            {
                target = settings.Stations;
            }
            else if (settings.Labels.Enabled)
            {
                target = settings.Labels;
            }

            if (target is null)
            {
                return 0;
            }

            var position = CrossAxisGeometry.ComputePlacement(origin, along, across, target);
            return CrossAxisAnnotationBinder.TrySetTextPosition(label, position) ? 1 : 0;
        }

        var updated = 0;

        if (settings.Labels.Enabled && label is not null)
        {
            var position = CrossAxisGeometry.ComputePlacement(origin, along, across, settings.Labels);
            if (CrossAxisAnnotationBinder.TrySetTextPosition(label, position))
            {
                updated++;
            }
        }

        if (settings.Stations.Enabled && chainage is not null)
        {
            var position = CrossAxisGeometry.ComputePlacement(origin, along, across, settings.Stations);
            if (CrossAxisAnnotationBinder.TrySetTextPosition(chainage, position))
            {
                updated++;
            }
        }

        // Fallback: stari CX stubovi (ako nema RoleText/CHNG).
        if (updated == 0)
        {
            FindExistingCrossTexts(tr, db, axisNumber, origin, out var cxLabel, out var cxStation);
            if (settings.Labels.Enabled && cxLabel is not null)
            {
                var position = CrossAxisGeometry.ComputePlacement(origin, along, across, settings.Labels);
                if (CrossAxisAnnotationBinder.TrySetTextPosition(cxLabel, position))
                {
                    updated++;
                }
            }

            if (settings.Stations.Enabled && cxStation is not null)
            {
                var position = CrossAxisGeometry.ComputePlacement(origin, along, across, settings.Stations);
                if (CrossAxisAnnotationBinder.TrySetTextPosition(cxStation, position))
                {
                    updated++;
                }
            }
        }

        return updated;
    }

    /// <summary>
    /// Razdvaja "OSA 20 0-380.00" u RoleText + RoleChainage (isti sloj/rotacija). Ne brise nista.
    /// </summary>
    private static Entity SplitCombinedRoadLabel(
        Transaction tr,
        Database db,
        Entity combined,
        string prefix,
        int number,
        string chainageText)
    {
        if (!RoadXData.TryReadStationLabel(combined, out var axisName, out _, out var station))
        {
            axisName = string.Empty;
            station = 0;
        }

        var labelText = string.IsNullOrWhiteSpace(prefix)
            ? number.ToString()
            : $"{prefix.Trim()} {number}";

        if (combined is DBText dbText)
        {
            if (!dbText.IsWriteEnabled)
            {
                dbText.UpgradeOpen();
            }

            dbText.TextString = labelText;
            RoadXData.AttachStationLabel(dbText, axisName, RoadXData.RoleText, station);

            var lineSpacing = dbText.Height * 1.35;
            var textDir = new Vector3d(Math.Cos(dbText.Rotation), Math.Sin(dbText.Rotation), 0);
            var stackDir = new Vector3d(-textDir.Y, textDir.X, 0);
            if (stackDir.Length > 1e-9)
            {
                stackDir = stackDir.GetNormal();
            }

            var basePos = dbText.Position;
            if (dbText.HorizontalMode != TextHorizontalMode.TextLeft ||
                dbText.VerticalMode != TextVerticalMode.TextBase)
            {
                basePos = dbText.AlignmentPoint;
            }

            dbText.HorizontalMode = TextHorizontalMode.TextCenter;
            dbText.VerticalMode = TextVerticalMode.TextVerticalMid;
            dbText.AlignmentPoint = basePos - stackDir * (lineSpacing * 0.5);
            dbText.AdjustAlignment(db);

            var clone = new DBText
            {
                Height = dbText.Height,
                Rotation = dbText.Rotation,
                TextString = chainageText,
                Layer = dbText.Layer,
                Color = dbText.Color,
                TextStyleId = dbText.TextStyleId,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid
            };
            clone.AlignmentPoint = basePos + stackDir * (lineSpacing * 0.5);
            clone.AdjustAlignment(db);

            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);
            modelSpace.AppendEntity(clone);
            tr.AddNewlyCreatedDBObject(clone, true);
            RoadXData.AttachStationLabel(clone, axisName, RoadXData.RoleChainage, station);
            return clone;
        }

        return combined;
    }

    private static Entity? FindRoadStationPart(
        Transaction tr,
        Database db,
        string? roadAxisName,
        double? stationValue,
        Point3d origin,
        string role,
        double searchRadius = 100.0)
    {
        Entity? exact = null;
        Entity? nearest = null;
        var bestDistance = double.MaxValue;

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var axisName, out var entityRole, out var station) ||
                !string.Equals(entityRole, role, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(roadAxisName) &&
                !string.Equals(axisName, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!CrossAxisAnnotationBinder.TryGetTextContent(tr, entity, out _, out var position))
            {
                continue;
            }

            if (stationValue is double expected && Math.Abs(station - expected) <= 1e-2)
            {
                exact = entity;
                break;
            }

            if (stationValue is null)
            {
                var distance = origin.DistanceTo(position);
                if (distance <= Math.Min(searchRadius, 40.0) && distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = entity;
                }
            }
        }

        return exact ?? nearest;
    }

    private static void FindExistingCrossTexts(
        Transaction tr,
        Database db,
        int axisNumber,
        Point3d origin,
        out Entity? label,
        out Entity? station)
    {
        label = null;
        station = null;
        var bestLabel = double.MaxValue;
        var bestStation = double.MaxValue;

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            // Ne uzimaj RoleText/CHNG ovde — njih pomera RepositionExistingStationTexts.
            if (RoadXData.TryReadStationLabel(entity, out _, out var stationRole, out _) &&
                (stationRole == RoadXData.RoleText || stationRole == RoadXData.RoleChainage))
            {
                continue;
            }

            if (!CrossAxisXData.TryReadCrossAnnotation(entity, out var role, out var linked) ||
                linked != axisNumber)
            {
                continue;
            }

            if (!CrossAxisAnnotationBinder.TryGetTextContent(tr, entity, out _, out var position))
            {
                continue;
            }

            var distance = origin.DistanceTo(position);
            if (role == CrossAxisXData.RoleCrossLabel && distance < bestLabel)
            {
                bestLabel = distance;
                label = entity;
            }
            else if (role == CrossAxisXData.RoleCrossStation && distance < bestStation)
            {
                bestStation = distance;
                station = entity;
            }
        }
    }

    private static double? ResolveStationValueForNumber(
        Transaction tr,
        Database db,
        string roadAxisName,
        int axisNumber,
        StationLabelOptions options)
    {
        var metadata = RoadAxisStore.Load(tr, db, roadAxisName);
        if (metadata is null)
        {
            return null;
        }

        RoadAxis? axis = null;
        if (metadata.HasSourcePolyline &&
            AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out var polylineId))
        {
            var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
            axis = PolylineToTangentConverter.Convert(polyline, metadata.CurveRadius, 0, roadAxisName);
            axis = RoadAxisTrimmer.Trim(axis, metadata.StartStation, metadata.EndStation);
        }
        else
        {
            axis = AxisGeometryReader.ReadAxis(tr, db, roadAxisName, metadata.StartStation);
        }

        if (axis is null || axis.Elements.Count == 0)
        {
            return null;
        }

        var stations = RoadDrawing.CollectStationsForSync(axis, options);
        var index = axisNumber - options.AxisCounterStart;
        if (index < 0 || index >= stations.Count)
        {
            return null;
        }

        return stations[index];
    }

    private static DBText CreateAnnotationText(
        Transaction tr,
        Database db,
        string contents,
        Point3d position)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        EnsureLayer(tr, db);
        var text = new DBText
        {
            Position = position,
            Height = 2.5,
            TextString = contents,
            Layer = CrossAxisScanner.LayerName
        };
        modelSpace.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
        return text;
    }

    private static void EnsureLayer(Transaction tr, Database db)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(CrossAxisScanner.LayerName))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = CrossAxisScanner.LayerName,
            Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                5)
        };
        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }
}
