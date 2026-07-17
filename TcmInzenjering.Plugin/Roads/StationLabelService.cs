using Autodesk.AutoCAD.DatabaseServices;
using TcmInzenjering.Plugin.Roads.CrossAxis;
using TcmInzenjering.Plugin.Roads.Profile;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Roads;

internal static class StationLabelService
{
    public static int RefreshAxis(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            return 0;
        }

        if (metadata.HasSourcePolyline &&
            AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out var polylineId))
        {
            return RefreshAxisFromPolyline(tr, db, axisName, metadata, polylineId);
        }

        return RefreshAxisGeometry(tr, db, axisName);
    }

    public static int RefreshAxisGeometry(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            return 0;
        }

        RoadDrawing.RunWithUnlockedAxisLayer(tr, db, () =>
        {
            AxisGeometryUpdater.Reconcile(
                tr,
                db,
                axisName,
                metadata.CurveRadius,
                metadata.StartStation);
        });

        // Ako postoje SAS parametri, ponovo ih primeni (Reconcile radi samo kružne lukove).
        ReapplySavedCornerCurves(tr, db, axisName, metadata);

        var count = RefreshLabels(tr, db, axisName, metadata);
        UpdateAxisReference(tr, db, axisName, metadata.StartStation);
        count += TerrainProjectionRefresh.RefreshIfExists(tr, db, axisName);
        count += ProfileViewRefresh.RefreshIfExists(tr, db, axisName);
        RoadDrawing.EnsureTangentOnTop(tr, db, axisName);
        return count;
    }

    private static void ReapplySavedCornerCurves(
        Transaction tr,
        Database db,
        string axisName,
        RoadAxisMetadata metadata)
    {
        var curves = CornerCurveStore.Load(tr, db, axisName);
        if (!curves.Values.Any(c => c.L1 > 1e-6 || c.L2 > 1e-6))
        {
            return;
        }

        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        if (axis is null)
        {
            return;
        }

        var withSas = CornerCurveApplicator.ApplySavedCurves(tr, db, axis);
        if (withSas.Elements.Count(e => e.Type == AlignmentElementType.Spiral) == 0)
        {
            return;
        }

        ObjectId sourceId = ObjectId.Null;
        if (metadata.HasSourcePolyline)
        {
            AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out sourceId);
        }

        var color = metadata.AxisColorIndex;
        RoadDrawing.RunWithUnlockedAxisLayer(tr, db, () =>
        {
            DeleteAxisEntities(tr, db, axisName);
            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);
            RoadDrawing.DrawAxisCore(tr, modelSpace, withSas, sourceId, color);
        });
    }

    /// <summary>
    /// Posle ručne izmene R na čvoru: stacionaže, tabele, 3D projekcija, podužni.
    /// </summary>
    public static int RefreshAfterCornerEdit(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            return 0;
        }

        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        if (axis is not null && axis.Elements.Count > 0)
        {
            var newEnd = axis.Elements[^1].EndStation;
            if (Math.Abs(newEnd - metadata.EndStation) > 1e-3 ||
                newEnd > metadata.EndStation + 1e-3)
            {
                metadata = CloneMetadataWithEnd(metadata, newEnd);
                RoadAxisStore.Save(tr, db, metadata);
            }
        }

        var count = RefreshLabels(tr, db, axisName, metadata);

        axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        if (axis is not null)
        {
            CrossAxisDrawService.EnsureEndStationCrossAxis(tr, db, axisName, axis, metadata);
        }

        UpdateAxisReference(tr, db, axisName, metadata.StartStation);
        count += TerrainProjectionRefresh.RefreshIfExists(tr, db, axisName);
        count += ProfileViewRefresh.RefreshIfExists(tr, db, axisName);
        RoadDrawing.EnsureTangentOnTop(tr, db, axisName);
        return count;
    }

    private static RoadAxisMetadata CloneMetadataWithEnd(RoadAxisMetadata metadata, double endStation) =>
        new()
        {
            Name = metadata.Name,
            StartStation = metadata.StartStation,
            EndStation = endStation,
            Interval = metadata.Interval,
            TickLength = metadata.TickLength,
            TextHeight = metadata.TextHeight,
            Prefix = metadata.Prefix,
            LabelSideSign = metadata.LabelSideSign,
            CurveRadius = metadata.CurveRadius,
            EqualIntervalInBounds = metadata.EqualIntervalInBounds,
            WholeInterval = metadata.WholeInterval,
            AlignToStart = metadata.AlignToStart,
            LabelAtStart = metadata.LabelAtStart,
            LabelAtEnd = true,
            LabelAtMainPoints = metadata.LabelAtMainPoints,
            SourcePolylineHandle = metadata.SourcePolylineHandle,
            PolylineStartDistance = metadata.PolylineStartDistance,
            PolylineEndDistance = metadata.PolylineEndDistance,
            PolylineReferenceLength = metadata.PolylineReferenceLength,
            AxisCounterStart = metadata.AxisCounterStart,
            LabelFormat = metadata.LabelFormat,
            ChainageFormat = metadata.ChainageFormat,
            DrawSegmentLabels = metadata.DrawSegmentLabels,
            AxisColorIndex = metadata.AxisColorIndex,
            StationTextColorIndex = metadata.StationTextColorIndex,
            StationTickColorIndex = metadata.StationTickColorIndex,
            SegmentLabelColorIndex = metadata.SegmentLabelColorIndex
        };

    public static int RefreshAxisFromPolyline(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null ||
            !metadata.HasSourcePolyline ||
            !AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out var polylineId))
        {
            return 0;
        }

        return RefreshAxisFromPolyline(tr, db, axisName, metadata, polylineId);
    }

    public static int RefreshLabels(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            return 0;
        }

        return RefreshLabels(tr, db, axisName, metadata);
    }

    private static int RefreshAxisFromPolyline(
        Transaction tr,
        Database db,
        string axisName,
        RoadAxisMetadata metadata,
        ObjectId polylineId)
    {
        var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
        var cornerRadii = CornerRadiusStore.Load(tr, db, axisName);
        var fullAxis = PolylineToTangentConverter.Convert(
            polyline,
            metadata.CurveRadius,
            0,
            axisName,
            cornerRadii);
        // Sačuvane prelaznice (L1/R/L2) — ne gube se pri pomeranju izvorne polilinije.
        fullAxis = CornerCurveApplicator.ApplySavedCurves(tr, db, fullAxis);
        var stationOptions = metadata.ToLabelOptions(polyline, fullAxis);
        var visibleAxis = RoadAxisTrimmer.Trim(
            fullAxis,
            stationOptions.StartStation,
            stationOptions.EndStation);

        DeleteLabels(tr, db, axisName);
        DeleteCrossAnnotations(tr, db, axisName);
        DeleteRadiusLabels(tr, db, axisName);
        DeleteSegmentLabels(tr, db, axisName);
        DeleteTangentNodeTables(tr, db, axisName);

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var stationCount = 0;
        var segmentCount = 0;
        var nodeCount = 0;
        RoadDrawing.RunWithUnlockedAxisLayer(tr, db, () =>
        {
            DeleteAxisEntities(tr, db, axisName);
            RoadDrawing.DrawAxisCore(tr, modelSpace, visibleAxis, polylineId, stationOptions.AxisColorIndex);
            stationCount = RoadDrawing.DrawStationLabels(tr, modelSpace, visibleAxis, stationOptions);
            segmentCount = metadata.DrawSegmentLabels
                ? RoadDrawing.DrawSegmentLabels(
                    tr,
                    modelSpace,
                    visibleAxis,
                    metadata.TextHeight,
                    metadata.LabelSideSign,
                    stationOptions.SegmentLabelColorIndex)
                : 0;
            nodeCount = RoadDrawing.DrawTangentNodeTables(tr, modelSpace, visibleAxis, metadata.TextHeight);
        });

        CrossAxisLayoutService.SyncToRoadAxis(tr, db, axisName, visibleAxis, stationOptions);
        CrossAxisDrawService.RestoreManualCrossAxisAnnotations(
            tr, db, axisName, visibleAxis, metadata, stationOptions);
        var polylineForWrite = (Polyline)tr.GetObject(polylineId, OpenMode.ForWrite);
        RoadXData.AttachSourcePolyline(polylineForWrite, axisName);
        RoadDrawing.StyleSourcePolyline(tr, db, polylineForWrite);
        RoadDrawing.EnsureTangentOnTop(tr, db, axisName);

        var polylineStart = metadata.PolylineStartDistance;
        var polylineEnd = metadata.PolylineEndDistance;
        if (metadata.HasSourcePolyline)
        {
            (polylineStart, polylineEnd) = metadata.ResolvePolylineSpan(polyline);
        }

        RoadAxisStore.Save(tr, db, new RoadAxisMetadata
        {
            Name = metadata.Name,
            StartStation = stationOptions.StartStation,
            EndStation = stationOptions.EndStation,
            Interval = metadata.Interval,
            TickLength = metadata.TickLength,
            TextHeight = metadata.TextHeight,
            Prefix = metadata.Prefix,
            LabelSideSign = metadata.LabelSideSign,
            CurveRadius = metadata.CurveRadius,
            EqualIntervalInBounds = metadata.EqualIntervalInBounds,
            WholeInterval = metadata.WholeInterval,
            AlignToStart = metadata.AlignToStart,
            LabelAtStart = metadata.LabelAtStart,
            LabelAtEnd = metadata.LabelAtEnd,
            LabelAtMainPoints = metadata.LabelAtMainPoints,
            SourcePolylineHandle = metadata.SourcePolylineHandle,
            PolylineStartDistance = polylineStart,
            PolylineEndDistance = polylineEnd,
            PolylineReferenceLength = polyline.Length,
            AxisCounterStart = metadata.AxisCounterStart,
            LabelFormat = metadata.LabelFormat,
            ChainageFormat = metadata.ChainageFormat,
            DrawSegmentLabels = metadata.DrawSegmentLabels,
            AxisColorIndex = stationOptions.AxisColorIndex,
            StationTextColorIndex = stationOptions.StationTextColorIndex,
            StationTickColorIndex = stationOptions.StationTickColorIndex,
            SegmentLabelColorIndex = stationOptions.SegmentLabelColorIndex
        });

        UpdateAxisReference(tr, db, axisName, stationOptions.StartStation);
        var projected = TerrainProjectionRefresh.RefreshIfExists(tr, db, axisName);
        var profiles = ProfileViewRefresh.RefreshIfExists(tr, db, axisName);
        RoadDrawing.EnsureTangentOnTop(tr, db, axisName);
        return stationCount + segmentCount + nodeCount + projected + profiles;
    }

    private static int RefreshLabels(Transaction tr, Database db, string axisName, RoadAxisMetadata metadata)
    {
        // Drawn axis is trimmed to [StartStation, EndStation] on the full chainage.
        // Re-read with the same origin so 0+000 stays at the visible axis start after a move.
        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        if (axis is null)
        {
            return 0;
        }

        DeleteLabels(tr, db, axisName);
        DeleteCrossAnnotations(tr, db, axisName);
        DeleteRadiusLabels(tr, db, axisName);
        DeleteSegmentLabels(tr, db, axisName);
        DeleteTangentNodeTables(tr, db, axisName);
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var stationCount = RoadDrawing.DrawStationLabels(
            tr,
            modelSpace,
            axis,
            metadata.ToLabelOptions());

        var segmentCount = metadata.DrawSegmentLabels
            ? RoadDrawing.DrawSegmentLabels(
                tr,
                modelSpace,
                axis,
                metadata.TextHeight,
                metadata.LabelSideSign,
                metadata.SegmentLabelColorIndex)
            : 0;

        var nodeCount = RoadDrawing.DrawTangentNodeTables(tr, modelSpace, axis, metadata.TextHeight);

        CrossAxisLayoutService.SyncToRoadAxis(tr, db, axisName, axis, metadata.ToLabelOptions());
        CrossAxisDrawService.RestoreManualCrossAxisAnnotations(
            tr, db, axisName, axis, metadata, metadata.ToLabelOptions());

        return stationCount + segmentCount + nodeCount;
    }

    private static void UpdateAxisReference(
        Transaction tr,
        Database db,
        string axisName,
        double startStation)
    {
        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
        if (axis is null || axis.Elements.Count == 0)
        {
            return;
        }

        AxisReferenceTracker.Update(axisName, axis.Elements[0].Start);
    }

    public static void DeleteAxisEntities(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer != RoadDrawing.AxisLayerName ||
                !RoadXData.TryReadAxisElement(entity, out var name, out _))
            {
                continue;
            }

            if (string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            entity.Erase();
        }
    }

    public static void DeleteRadiusLabels(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer != RoadDrawing.RadiusLayerName ||
                !RoadXData.TryReadRadiusAnnotation(entity, out var name, out _))
            {
                continue;
            }

            if (string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            entity.Erase();
        }
    }

    public static void DeleteLabels(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer != RoadDrawing.StationLayerName ||
                !RoadXData.TryReadStationLabel(entity, out var name, out _, out _))
            {
                continue;
            }

            if (string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            entity.Erase();
        }
    }

    public static void DeleteCrossAnnotations(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased)
            {
                continue;
            }

            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (!CrossAxisXData.TryReadCrossAnnotation(entity, out _, out _, out var parent))
            {
                continue;
            }

            // Stari split (bez parent-a) + oznake vezane za ovu putnu ose.
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase.Distinct())
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            if (!entity.IsErased)
            {
                entity.Erase();
            }
        }
    }

    public static void DeleteSegmentLabels(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer != RoadDrawing.SegmentLayerName ||
                !RoadXData.TryReadSegmentLabel(entity, out var name))
            {
                continue;
            }

            if (string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            entity.Erase();
        }
    }

    public static void DeleteTangentNodeTables(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased)
            {
                continue;
            }

            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer != RoadDrawing.TangentNodeLayerName ||
                !RoadXData.TryReadTangentNode(entity, out var name, out _))
            {
                continue;
            }

            if (string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            if (!entity.IsErased)
            {
                entity.Erase();
            }
        }
    }
}