using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>Civil-lite Slope / Watershed analiza nad TCM 3DFACE TIN-om.</summary>
public sealed partial class RoadCommands
{
    public const string SlopeArrowLayer = "TCM_SLOPE_ARROW";
    public const string WatershedLayer = "TCM_WATERSHED";

    /// <summary>Pokreće Slope analizu (boje + strelice) na aktivnom TIN-u.</summary>
    [CommandMethod("TCMTERSLOPE", CommandFlags.Modal)]
    public void RunTerrainSlopeAnalysis()
    {
        ContourPreferences.Load();
        var style = ContourPreferences.Current.Clone();
        style.AnalyzeSlopes = true;
        style.AnalyzeSlopeArrows = true;
        style.GetComponent("Slopes").Visible = true;
        style.GetComponent("Slope Arrows").Visible = true;
        ContourPreferences.Save(style);
        ApplyCurrentContourStyleToDrawing(writeMessage: true);
    }

    /// <summary>Pokreće Watershed (slivove) na aktivnom TIN-u.</summary>
    [CommandMethod("TCMTERWSHD", CommandFlags.Modal)]
    public void RunTerrainWatershedAnalysis()
    {
        ContourPreferences.Load();
        var style = ContourPreferences.Current.Clone();
        style.ShowWatersheds = true;
        style.GetComponent("Watersheds").Visible = true;
        ContourPreferences.Save(style);
        ApplyCurrentContourStyleToDrawing(writeMessage: true);
    }

    /// <summary>
    /// Boji 3DFACE po nagibu/elevaciji, crta slope strelice i watershed outline-e
    /// prema Analysis / Display komponentama.
    /// </summary>
    internal static (int Arrows, int Watersheds, int FacesColored) ApplyTerrainAnalysisOverlays(
        Transaction tr,
        Database db,
        SurfaceStyleSnapshot style,
        IReadOnlyList<TerrainFacePart> faceParts)
    {
        var slopesOn = style.AnalyzeSlopes || style.GetComponent("Slopes").Visible;
        var arrowsOn = style.AnalyzeSlopeArrows || style.GetComponent("Slope Arrows").Visible;
        var directionsOn = style.AnalyzeDirections || style.GetComponent("Directions").Visible;
        var elevOn = style.AnalyzeElevations || style.GetComponent("Elevations").Visible;
        var watershedOn = style.ShowWatersheds || style.GetComponent("Watersheds").Visible;

        var erasedArrows = EraseEntitiesWithAnalysisRole(tr, db, TerrainAnalysisXData.RoleSlopeArrow);
        var erasedWshd = EraseEntitiesWithAnalysisRole(tr, db, TerrainAnalysisXData.RoleWatershed);
        _ = erasedArrows;
        _ = erasedWshd;

        if (faceParts.Count == 0)
        {
            return (0, 0, 0);
        }

        var triangles = faceParts.Select(p => p.Triangle).ToList();
        var samples = triangles.Select(TerrainSlopeMath.Analyze).ToList();

        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var s in samples)
        {
            zMin = Math.Min(zMin, s.Centroid.Z);
            zMax = Math.Max(zMax, s.Centroid.Z);
        }

        TerrainWatershedResult? watershed = null;
        if (watershedOn)
        {
            watershed = TerrainWatershedBuilder.Build(triangles);
        }

        // Face bojenje: Slope > Elevation > Watershed tint > restore ByLayer.
        var facesColored = 0;
        var seenFaces = new HashSet<ObjectId>();
        for (var i = 0; i < faceParts.Count; i++)
        {
            var faceId = faceParts[i].FaceId;
            if (!seenFaces.Add(faceId))
            {
                continue;
            }

            if (tr.GetObject(faceId, OpenMode.ForWrite) is not Face face || face.IsErased)
            {
                continue;
            }

            short aci;
            bool colorize;
            if (slopesOn)
            {
                aci = TerrainSlopeMath.SlopePercentToAci(samples[i].SlopePercent);
                colorize = true;
            }
            else if (elevOn)
            {
                aci = TerrainSlopeMath.ElevationBandToAci(samples[i].Centroid.Z, zMin, zMax);
                colorize = true;
            }
            else if (watershedOn && watershed is not null)
            {
                aci = TerrainSlopeMath.BasinIdToAci(watershed.BasinIdByTriangle[i]);
                colorize = true;
            }
            else
            {
                face.Color = AcColor.FromColorIndex(ColorMethod.ByLayer, 0);
                continue;
            }

            if (colorize)
            {
                face.Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci);
                face.Visible = true;
                facesColored++;
            }
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var arrowCount = 0;
        if (arrowsOn || directionsOn)
        {
            var arrowComp = style.GetComponent("Slope Arrows");
            if (directionsOn && !arrowsOn)
            {
                arrowComp = style.GetComponent("Directions");
            }

            EnsureNamedLayer(tr, db, SlopeArrowLayer,
                arrowComp.ColorByLayer ? (short)7 : arrowComp.ColorAci, 0);

            for (var i = 0; i < faceParts.Count; i++)
            {
                var sample = samples[i];
                if (sample.IsFlat || sample.FlowDirection.Length < 1e-12)
                {
                    continue;
                }

                var len = ArrowLength(triangles[i]);
                if (len < 1e-6)
                {
                    continue;
                }

                var c = sample.Centroid;
                var tip = new Point3d(
                    c.X + sample.FlowDirection.X * len,
                    c.Y + sample.FlowDirection.Y * len,
                    c.Z);
                var line = new Line(c, tip);
                ApplyAnalysisComponentStyle(tr, db, line, arrowComp, SlopeArrowLayer);
                modelSpace.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
                TerrainAnalysisXData.AttachSlopeArrow(line, sample.SlopePercent);

                // Mali „V“ arrowhead.
                var hx = -sample.FlowDirection.X;
                var hy = -sample.FlowDirection.Y;
                var headLen = len * 0.28;
                var ox = -hy * 0.35;
                var oy = hx * 0.35;
                AddArrowHeadLine(tr, db, modelSpace, tip,
                    new Point3d(tip.X + hx * headLen + ox * headLen, tip.Y + hy * headLen + oy * headLen, tip.Z),
                    arrowComp, sample.SlopePercent);
                AddArrowHeadLine(tr, db, modelSpace, tip,
                    new Point3d(tip.X + hx * headLen - ox * headLen, tip.Y + hy * headLen - oy * headLen, tip.Z),
                    arrowComp, sample.SlopePercent);
                arrowCount++;
            }
        }

        var wshdCount = 0;
        if (watershedOn && watershed is not null)
        {
            var wComp = style.GetComponent("Watersheds");
            EnsureNamedLayer(tr, db, WatershedLayer,
                wComp.ColorByLayer ? (short)1 : wComp.ColorAci, 0.35);

            foreach (var ring in watershed.BasinOutlines)
            {
                if (ring.Count < 3)
                {
                    continue;
                }

                var elev = AverageElevationOnRing(triangles, ring);
                var pl = new Polyline(ring.Count)
                {
                    Elevation = elev,
                    Closed = PointsClose(ring[0], ring[^1]) || PointsClose(ring[0], ring[ring.Count - 1])
                };

                // Ako lanac nije zatvoren, zatvori ga.
                var closed = PointsClose(ring[0], ring[^1]);
                for (var i = 0; i < ring.Count; i++)
                {
                    if (closed && i == ring.Count - 1)
                    {
                        break;
                    }

                    pl.AddVertexAt(pl.NumberOfVertices, ring[i], 0, 0, 0);
                }

                if (pl.NumberOfVertices >= 3)
                {
                    pl.Closed = true;
                    ApplyAnalysisComponentStyle(tr, db, pl, wComp, WatershedLayer);
                    modelSpace.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    TerrainAnalysisXData.AttachWatershed(pl, wshdCount);
                    wshdCount++;
                }
                else
                {
                    pl.Dispose();
                }
            }
        }

        return (arrowCount, wshdCount, facesColored);
    }

    private static void AddArrowHeadLine(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        Point3d from,
        Point3d to,
        SurfaceComponentStyle comp,
        double slopePercent)
    {
        var line = new Line(from, to);
        ApplyAnalysisComponentStyle(tr, db, line, comp, SlopeArrowLayer);
        modelSpace.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        TerrainAnalysisXData.AttachSlopeArrow(line, slopePercent);
    }

    private static void ApplyAnalysisComponentStyle(
        Transaction tr,
        Database db,
        Entity entity,
        SurfaceComponentStyle comp,
        string defaultLayer)
    {
        var layerName = string.IsNullOrWhiteSpace(comp.Layer) || comp.Layer == "0"
            ? defaultLayer
            : comp.Layer.Trim();
        EnsureNamedLayer(tr, db, layerName,
            comp.ColorByLayer ? (short)7 : comp.ColorAci,
            comp.LineWeightByBlock ? 0 : comp.LineWeightMm);
        entity.Layer = layerName;
        if (comp.ColorByLayer)
        {
            entity.Color = AcColor.FromColorIndex(ColorMethod.ByLayer, 0);
        }
        else
        {
            entity.Color = AcColor.FromColorIndex(ColorMethod.ByAci, comp.ColorAci);
        }

        entity.Linetype = "ByLayer";
        entity.LineWeight = comp.LineWeightByBlock
            ? LineWeight.ByBlock
            : MmToLineWeight(comp.LineWeightMm);
    }

    private static double ArrowLength(TerrainTriangle t)
    {
        var e1 = t.A.DistanceTo(t.B);
        var e2 = t.B.DistanceTo(t.C);
        var e3 = t.C.DistanceTo(t.A);
        var minEdge = Math.Min(e1, Math.Min(e2, e3));
        return Math.Max(1e-3, minEdge * 0.40);
    }

    private static double AverageElevationOnRing(
        IReadOnlyList<TerrainTriangle> triangles,
        IReadOnlyList<Point2d> ring)
    {
        double sum = 0;
        var count = 0;
        foreach (var p in ring)
        {
            foreach (var tri in triangles)
            {
                if (tri.TryGetElevation(p.X, p.Y, out var z))
                {
                    sum += z;
                    count++;
                    break;
                }
            }
        }

        if (count > 0)
        {
            return sum / count;
        }

        return triangles.Count > 0
            ? (triangles[0].A.Z + triangles[0].B.Z + triangles[0].C.Z) / 3.0
            : 0;
    }

    private static bool PointsClose(Point2d a, Point2d b) =>
        Math.Abs(a.X - b.X) < 1e-4 && Math.Abs(a.Y - b.Y) < 1e-4;

    private static int EraseEntitiesWithAnalysisRole(Transaction tr, Database db, string role)
    {
        var count = 0;
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                continue;
            }

            if (!TerrainAnalysisXData.TryGetRole(entity, out var r) || r != role)
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Erase();
            count++;
        }

        return count;
    }

    internal static List<TerrainFacePart> LoadTerrainFacePartsFromModel(Transaction tr, Database db)
    {
        var parts = new List<TerrainFacePart>();
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Face face || face.IsErased)
            {
                continue;
            }

            if (!TerrainFaceXData.IsTerrainFace(face) &&
                !string.Equals(face.Layer, TerrainLayerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var p0 = face.GetVertexAt(0);
            var p1 = face.GetVertexAt(1);
            var p2 = face.GetVertexAt(2);
            var p3 = face.GetVertexAt(3);
            parts.Add(new TerrainFacePart(id, new TerrainTriangle(p0, p1, p2)));
            if (p3.DistanceTo(p2) > 1e-9 && p3.DistanceTo(p0) > 1e-9)
            {
                parts.Add(new TerrainFacePart(id, new TerrainTriangle(p0, p2, p3)));
            }
        }

        return parts;
    }
}
