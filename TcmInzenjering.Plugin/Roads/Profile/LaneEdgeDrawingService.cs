using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>Crta i osvežava ivice / granice / hatch aktivnog kolovoza uz TCM osu.</summary>
internal static class LaneEdgeDrawingService
{
    public const string LayerName = "TCM_IVICE_KOLOVOZA";
    public const string BoundaryLayerName = "TCM_GRANICE_TRAKA";
    public const string ShoulderLayerName = "TCM_IVICE_BANKINE";
    public const string HatchLayerName = "TCM_KOLOVOZ_HATCH";

    public static int RefreshIfExists(
        Transaction tr,
        Database db,
        string axisName)
    {
        if (!LaneWidthDefinitionStore.HasSavedDefinitions(tr, db, axisName))
        {
            return 0;
        }

        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            return 0;
        }

        var axis = AxisGeometryReader.ReadAxis(
            tr, db, axisName, metadata.StartStation);
        if (axis is null || axis.Elements.Count == 0)
        {
            return 0;
        }

        ProfileLaneWidthStore.TryGetDefaults(
            tr, db, axisName, out var fallbackLeft, out var fallbackRight);
        var definitions = LaneWidthDefinitionStore.Load(
            tr, db, axisName, fallbackLeft, fallbackRight);
        return Redraw(tr, db, axis, definitions);
    }

    /// <summary>Legacy API — crta samo aktivni tip konstantno.</summary>
    public static int Redraw(
        Transaction tr,
        Database db,
        RoadAxis axis,
        LaneWidthType activeType)
    {
        var set = new LaneWidthDefinitionSet
        {
            ActiveTypeName = activeType.Name,
            Types = [activeType.Clone()],
            DrawBoundaries = true
        };
        return Redraw(tr, db, axis, set);
    }

    public static int Redraw(
        Transaction tr,
        Database db,
        RoadAxis axis,
        LaneWidthDefinitionSet definitions)
    {
        EnsureLayers(tr, db);
        EnsureRegApp(tr, db);
        EraseExisting(tr, db, axis.Name);

        var samples = LaneWidthEvaluator.Sample(definitions, axis, step: 2.0);
        if (samples.Count < 2)
        {
            return 0;
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        // Ključevi offset granica: OUTER_L, OUTER_R, INNER boundaries by lane id outer.
        var tracks = new Dictionary<string, List<Point2d>>(StringComparer.OrdinalIgnoreCase);
        void AddPoint(string key, Point2d point)
        {
            if (!tracks.TryGetValue(key, out var list))
            {
                list = [];
                tracks[key] = list;
            }

            if (list.Count == 0 || list[^1].GetDistanceTo(point) > 1e-7)
            {
                list.Add(point);
            }
        }

        foreach (var (station, result) in samples)
        {
            if (!TryAxisFrame(axis, station, out var center, out var normal))
            {
                continue;
            }

            AddPoint("OUTER_L", To2d(center + normal * result.LeftCarriageway));
            AddPoint("OUTER_R", To2d(center - normal * result.RightCarriageway));

            foreach (var slice in result.Lanes)
            {
                var isShoulder = slice.Role == LaneRole.Shoulder;
                if (!isShoulder && !definitions.DrawBoundaries)
                {
                    continue;
                }

                // Unutrašnje granice traka i spoljne ivice bankina.
                var isOuterCarriageway =
                    slice.Role == LaneRole.Carriageway &&
                    Math.Abs(
                        (slice.Side == LaneSide.Left
                            ? result.LeftCarriageway
                            : result.RightCarriageway) - slice.OffsetFromAxisOuter) < 1e-6;
                if (isOuterCarriageway)
                {
                    continue;
                }

                if (slice.OffsetFromAxisOuter < 1e-6)
                {
                    continue;
                }

                // Normala pokazuje LEVO: leva strana = +offset, desna = -offset.
                var signed = slice.Side == LaneSide.Left
                    ? slice.OffsetFromAxisOuter
                    : -slice.OffsetFromAxisOuter;
                var sideKey = slice.Side == LaneSide.Left ? "L" : "D";
                var prefix = isShoulder ? "SH" : "B";
                AddPoint(
                    $"{prefix}_{sideKey}_{slice.LaneId}",
                    To2d(center + normal * signed));
            }
        }

        var drawn = 0;
        if (tracks.TryGetValue("OUTER_L", out var leftOuter))
        {
            drawn += AppendPolyline(
                tr, modelSpace, leftOuter, LayerName, axis.Name, "L", "OUTER_L", continuous: true);
        }

        if (tracks.TryGetValue("OUTER_R", out var rightOuter))
        {
            drawn += AppendPolyline(
                tr, modelSpace, rightOuter, LayerName, axis.Name, "D", "OUTER_R", continuous: true);
        }

        foreach (var pair in tracks)
        {
            if (pair.Key is "OUTER_L" or "OUTER_R")
            {
                continue;
            }

            var isShoulder = pair.Key.StartsWith("SH_", StringComparison.OrdinalIgnoreCase);
            if (!isShoulder && !definitions.DrawBoundaries)
            {
                continue;
            }

            var isLeft =
                pair.Key.StartsWith("SH_L_", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.StartsWith("B_L_", StringComparison.OrdinalIgnoreCase);
            drawn += AppendPolyline(
                tr,
                modelSpace,
                pair.Value,
                isShoulder ? ShoulderLayerName : BoundaryLayerName,
                axis.Name,
                isLeft ? "L" : "D",
                pair.Key,
                continuous: isShoulder);
        }

        if (definitions.Hatch.Enabled &&
            tracks.TryGetValue("OUTER_L", out var hatchLeft) &&
            tracks.TryGetValue("OUTER_R", out var hatchRight) &&
            hatchLeft.Count >= 2 && hatchRight.Count >= 2)
        {
            drawn += AppendHatch(
                tr, modelSpace, hatchLeft, hatchRight, axis.Name, definitions.Hatch);
        }

        return drawn;
    }

    private static int AppendPolyline(
        Transaction tr,
        BlockTableRecord modelSpace,
        IReadOnlyList<Point2d> points,
        string layer,
        string axisName,
        string side,
        string boundaryId,
        bool continuous)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        var polyline = new Polyline(points.Count)
        {
            Layer = layer,
            Elevation = 0,
            ConstantWidth = 0,
            Linetype = continuous ? "Continuous" : "DASHED"
        };
        try
        {
            // Ako DASHED nije učitan, ostavi Continuous.
            _ = polyline.Linetype;
        }
        catch
        {
            polyline.Linetype = "ByLayer";
        }

        for (var index = 0; index < points.Count; index++)
        {
            polyline.AddVertexAt(index, points[index], 0, 0, 0);
        }

        if (!continuous)
        {
            try
            {
                polyline.LinetypeScale = 0.5;
            }
            catch
            {
                // ignore
            }
        }

        modelSpace.AppendEntity(polyline);
        tr.AddNewlyCreatedDBObject(polyline, true);
        RoadXData.AttachLaneEdge(polyline, axisName, side, boundaryId);
        return 1;
    }

    private static int AppendHatch(
        Transaction tr,
        BlockTableRecord modelSpace,
        IReadOnlyList<Point2d> left,
        IReadOnlyList<Point2d> right,
        string axisName,
        LaneHatchSettings settings)
    {
        var loop = new List<Point2d>(left.Count + right.Count + 1);
        loop.AddRange(left);
        for (var i = right.Count - 1; i >= 0; i--)
        {
            loop.Add(right[i]);
        }

        if (loop.Count < 3)
        {
            return 0;
        }

        var boundary = new Polyline(loop.Count + 1)
        {
            Layer = HatchLayerName,
            Elevation = 0,
            Closed = true,
            Visible = false
        };
        for (var i = 0; i < loop.Count; i++)
        {
            boundary.AddVertexAt(i, loop[i], 0, 0, 0);
        }

        modelSpace.AppendEntity(boundary);
        tr.AddNewlyCreatedDBObject(boundary, true);
        RoadXData.AttachLaneHatch(boundary, axisName);

        try
        {
            var hatch = new Hatch
            {
                Layer = HatchLayerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, settings.ColorIndex)
            };
            modelSpace.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);
            hatch.SetHatchPattern(HatchPatternType.PreDefined, settings.Pattern);
            hatch.PatternScale = Math.Max(0.01, settings.Scale);
            hatch.PatternAngle = settings.Angle * Math.PI / 180.0;
            hatch.AppendLoop(
                HatchLoopTypes.Default,
                new ObjectIdCollection { boundary.ObjectId });
            hatch.EvaluateHatch(true);
            RoadXData.AttachLaneHatch(hatch, axisName);
            SendToBottom(tr, modelSpace, hatch.ObjectId, boundary.ObjectId);
            return 2;
        }
        catch
        {
            // Boundary ostaje kao fallback ako hatch pattern nije dostupan.
            boundary.Visible = true;
            SendToBottom(tr, modelSpace, boundary.ObjectId);
            return 1;
        }
    }

    private static void SendToBottom(
        Transaction tr,
        BlockTableRecord modelSpace,
        params ObjectId[] ids)
    {
        try
        {
            var drawOrder = (DrawOrderTable)tr.GetObject(
                modelSpace.DrawOrderTableId, OpenMode.ForWrite);
            var collection = new ObjectIdCollection();
            foreach (var id in ids)
            {
                if (!id.IsNull && !id.IsErased)
                {
                    collection.Add(id);
                }
            }

            if (collection.Count > 0)
            {
                drawOrder.MoveToBottom(collection);
            }
        }
        catch
        {
            // Redosled crtanja nije kritičan — ignoriši ako tabela nije dostupna.
        }
    }

    private static bool TryAxisFrame(
        RoadAxis axis,
        double station,
        out Point3d center,
        out Vector3d normal)
    {
        center = Point3d.Origin;
        normal = Vector3d.XAxis;
        var point = axis.GetPointAtStation(station);
        var direction = axis.GetDirectionAtStation(station) ??
                        axis.SampleDirectionAtStation(station);
        if (point is null || direction is null || direction.Value.Length < 1e-9)
        {
            return false;
        }

        center = point.Value;
        var unit = direction.Value.GetNormal();
        normal = new Vector3d(-unit.Y, unit.X, 0);
        return true;
    }

    private static Point2d To2d(Point3d point) => new(point.X, point.Y);

    private static void EraseExisting(
        Transaction tr,
        Database db,
        string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased)
            {
                continue;
            }

            var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (entity is null)
            {
                continue;
            }

            var isLane =
                RoadXData.TryReadLaneEdge(entity, out var ownerAxis) ||
                RoadXData.TryReadLaneHatch(entity, out ownerAxis);
            if (!isLane ||
                !string.Equals(ownerAxis, axisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Erase();
        }
    }

    private static void EnsureLayers(Transaction tr, Database db)
    {
        EnsureLayer(tr, db, LayerName, 4);
        EnsureLayer(tr, db, BoundaryLayerName, 8);
        EnsureLayer(tr, db, ShoulderLayerName, 3);
        EnsureLayer(tr, db, HatchLayerName, 8);
    }

    private static void EnsureLayer(Transaction tr, Database db, string name, short aci)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(name))
        {
            return;
        }

        table.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = Color.FromColorIndex(ColorMethod.ByAci, aci)
        };
        table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static void EnsureRegApp(Transaction tr, Database db)
    {
        var table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (table.Has(RoadDrawing.RegAppName))
        {
            return;
        }

        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = RoadDrawing.RegAppName };
        table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }
}
