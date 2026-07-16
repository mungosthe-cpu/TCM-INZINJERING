using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Civil-lite: izohipse (major/minor), kotne oznake, spot elevacija iz TCM 3DFACE TIN-a.
/// </summary>
public sealed partial class RoadCommands
{
    public const string ContourMinorLayer = "TCM_IZO_MINOR";
    public const string ContourMajorLayer = "TCM_IZO_MAJOR";
    public const string ContourLabelLayer = "TCM_IZO_LABEL";
    public const string SpotLayer = "TCM_SPOT";

    [CommandMethod("TCMTERIZOSET", CommandFlags.Modal)]
    public void TerrainContourSettings()
    {
        ContourPreferences.Load();
        var dialog = new ContourSettingsDialog();
        AcApp.ShowModalWindow(dialog);
    }

    [CommandMethod("TCMTERIZO", CommandFlags.Modal)]
    public void BuildTerrainContours()
    {
        ApplyCurrentContourStyleToDrawing(writeMessage: true);
    }

    /// <summary>
    /// Ponovo crta izohipse po trenutnom stilu. Poziva se i iz dijaloga podešavanja
    /// (bez LockDocument — isti modalni command kontekst).
    /// </summary>
    internal static bool ApplyCurrentContourStyleToDrawing(bool writeMessage = true)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return false;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        try
        {
            ContourPreferences.Load();
            var style = ContourPreferences.Current;
            var minor = style.MinorInterval;
            var major = style.MajorInterval;
            var baseElev = style.BaseElevation;
            var majorComp = style.GetComponent("Major Contour");
            var minorComp = style.GetComponent("Minor Contour");
            var userComp = style.GetComponent("User Contours");
            var triangleComp = style.GetComponent("Triangles");
            var anyContourVisible = majorComp.Visible || minorComp.Visible || userComp.Visible;
            var userElevs = userComp.Visible ? style.ParseUserContours() : Array.Empty<double>();

            using var tr = db.TransactionManager.StartTransaction();
            RoadDrawing.EnsureRegApp(tr, db);

            // Display → Triangles / Border: sakrij/prikaži (ne briši TIN/border).
            var faceVisCount = ApplyTerrainFaceVisibility(tr, db, triangleComp.Visible);
            var borderComp = style.GetComponent("Border");
            ApplyTerrainBorderVisibility(tr, db, borderComp.Visible && style.DisplayExteriorBorders);

            var faceParts = LoadTerrainFacePartsFromModel(tr, db);
            var triangles = faceParts.Count > 0
                ? faceParts.Select(p => p.Triangle).ToList()
                : LoadTerrainTrianglesFromModel(tr, db);
            if (triangles.Count == 0 && faceVisCount == 0)
            {
                if (writeMessage)
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Nema TCM 3DFACE terena. Prvo TCMTERFACE.");
                }

                tr.Commit();
                return false;
            }

            EnsureContourLayers(tr, db);
            var erased = EraseEntitiesWithRole(tr, db, TerrainContourXData.RoleContour);

            var minorCount = 0;
            var majorCount = 0;
            var userCount = 0;

            if (anyContourVisible && triangles.Count > 0)
            {
                var paths = TerrainContourBuilder.Build(triangles, minor, major, baseElev, userElevs);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);

                foreach (var path in paths)
                {
                    SurfaceComponentStyle comp;
                    if (path.IsUser)
                    {
                        if (!userComp.Visible)
                        {
                            continue;
                        }

                        comp = userComp;
                    }
                    else if (path.IsMajor)
                    {
                        if (!majorComp.Visible)
                        {
                            continue;
                        }

                        comp = majorComp;
                    }
                    else
                    {
                        if (!minorComp.Visible)
                        {
                            continue;
                        }

                        comp = minorComp;
                    }

                    var drawElevation = ResolveContourElevation(path.Elevation, style);
                    var points = ContourSmoother.Apply(
                        path.Points,
                        style.SmoothContours,
                        style.SmoothType,
                        style.SmoothFactor);

                    Entity entity;
                    if (style.SmoothContours &&
                        style.SmoothType == ContourSmoothType.SplineCurve &&
                        points.Count >= 3)
                    {
                        entity = CreateContourSpline(points, drawElevation);
                    }
                    else
                    {
                        entity = CreateContourPolyline(points, drawElevation);
                    }

                    ApplyComponentStyle(tr, db, entity, comp);
                    modelSpace.AppendEntity(entity);
                    tr.AddNewlyCreatedDBObject(entity, true);
                    TerrainContourXData.AttachContour(entity, path.Elevation, path.IsMajor || path.IsUser);
                    if (path.IsUser)
                    {
                        userCount++;
                    }
                    else if (path.IsMajor)
                    {
                        majorCount++;
                    }
                    else
                    {
                        minorCount++;
                    }
                }
            }

            var analysis = ApplyTerrainAnalysisOverlays(tr, db, style, faceParts);

            tr.Commit();
            try
            {
                ed.Regen();
            }
            catch
            {
                // regen nije kritičan
            }

            if (writeMessage)
            {
                var drawn = minorCount + majorCount + userCount;
                var analysisMsg =
                    (analysis.FacesColored > 0 ? $", slope/elev face {analysis.FacesColored}" : string.Empty) +
                    (analysis.Arrows > 0 ? $", strelice {analysis.Arrows}" : string.Empty) +
                    (analysis.Watersheds > 0 ? $", slivovi {analysis.Watersheds}" : string.Empty);

                if (!anyContourVisible && analysis.Arrows == 0 && analysis.Watersheds == 0 &&
                    analysis.FacesColored == 0)
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Izohipse isključene (Display)" +
                        (erased > 0 ? $", obrisano {erased}" : string.Empty) +
                        $", 3DFACE {(triangleComp.Visible ? "prikazani" : "sakriveni")}.");
                }
                else if (drawn == 0 && analysis.Arrows == 0 && analysis.Watersheds == 0 &&
                         analysis.FacesColored == 0)
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Nema kontura u opsegu elevacija" +
                        (erased > 0 ? $" (obrisano starih {erased})" : string.Empty) + ".");
                }
                else
                {
                    ed.WriteMessage(
                        $"\nTCM-INZINJERING: Izohipse — {minorCount} minor + {majorCount} major" +
                        (userCount > 0 ? $" + {userCount} user" : string.Empty) +
                        $" (interval {minor:0.###}/{major:0.###}, baza {baseElev:0.###}" +
                        (style.SmoothContours
                            ? $", smooth {style.SmoothType}/{style.SmoothFactor}"
                            : string.Empty) +
                        (erased > 0 ? $", obrisano starih {erased}" : string.Empty) +
                        $"), 3DFACE {(triangleComp.Visible ? "ON" : "OFF")}" +
                        analysisMsg + ".");
                }
            }

            return true;
        }
        catch (System.Exception ex)
        {
            if (writeMessage)
            {
                ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
            }

            return false;
        }
    }

    private static double ResolveContourElevation(double surfaceZ, SurfaceStyleSnapshot style) =>
        style.ContourDisplayMode switch
        {
            SurfaceElevationDisplayMode.FlattenToElevation => style.FlattenContoursElevation,
            SurfaceElevationDisplayMode.ExaggerateElevation =>
                style.BaseElevation + (surfaceZ - style.BaseElevation) * style.ExaggerateContoursScale,
            _ => surfaceZ
        };

    /// <summary>Civil Display → Triangles: Visible on/off na TCM 3DFACE (TIN ostaje u crtežu).</summary>
    private static int ApplyTerrainFaceVisibility(Transaction tr, Database db, bool visible)
    {
        var count = 0;
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

            count++;
            if (face.Visible == visible)
            {
                continue;
            }

            face.UpgradeOpen();
            face.Visible = visible;
        }

        return count;
    }

    /// <summary>Civil Display → Border: Visible on/off na TCM border poliliniji.</summary>
    private static void ApplyTerrainBorderVisibility(Transaction tr, Database db, bool visible)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                continue;
            }

            if (!TerrainBorderXData.IsTerrainBorder(entity))
            {
                continue;
            }

            if (entity.Visible == visible)
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Visible = visible;
        }
    }

    private static void ApplyComponentStyle(
        Transaction tr,
        Database db,
        Entity entity,
        SurfaceComponentStyle comp)
    {
        var layerName = string.IsNullOrWhiteSpace(comp.Layer) || comp.Layer == "0"
            ? ContourMinorLayer
            : comp.Layer.Trim();
        EnsureNamedLayer(tr, db, layerName,
            comp.ColorByLayer ? (short)7 : comp.ColorAci,
            comp.LineWeightByBlock ? 0 : comp.LineWeightMm);
        entity.Layer = layerName;
        if (comp.ColorByLayer)
        {
            entity.Color = AcColor.FromColorIndex(ColorMethod.ByLayer, 0);
        }
        else if (comp.ColorByBlock)
        {
            entity.Color = AcColor.FromColorIndex(ColorMethod.ByBlock, 0);
        }
        else
        {
            entity.Color = AcColor.FromColorIndex(ColorMethod.ByAci, comp.ColorAci);
        }
        entity.Linetype = ResolveLinetype(tr, db, comp.Linetype);
        entity.LinetypeScale = comp.LtScale > 0 ? comp.LtScale : 1.0;
        entity.LineWeight = comp.LineWeightByBlock
            ? LineWeight.ByBlock
            : MmToLineWeight(comp.LineWeightMm);
    }

    private static string ResolveLinetype(Transaction tr, Database db, string? name)
    {
        var lt = string.IsNullOrWhiteSpace(name) ? "Continuous" : name.Trim();
        if (lt.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
            lt.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
        {
            return lt;
        }

        var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(lt))
        {
            return lt;
        }

        return table.Has("Continuous") ? "Continuous" : "ByLayer";
    }

    private static Polyline CreateContourPolyline(IReadOnlyList<Point2d> points, double elevation)
    {
        var pl = new Polyline(points.Count)
        {
            Elevation = elevation,
            Closed = false
        };
        for (var i = 0; i < points.Count; i++)
        {
            pl.AddVertexAt(i, points[i], 0, 0, 0);
        }

        return pl;
    }

    private static Entity CreateContourSpline(IReadOnlyList<Point2d> points, double elevation)
    {
        var fit = new Point3dCollection();
        foreach (var p in points)
        {
            fit.Add(new Point3d(p.X, p.Y, elevation));
        }

        try
        {
            return new Spline(fit, 4, 0.0);
        }
        catch
        {
            return CreateContourPolyline(points, elevation);
        }
    }

    private static LineWeight MmToLineWeight(double mm)
    {
        if (mm <= 0)
        {
            return LineWeight.ByLayer;
        }

        // AutoCAD LineWeight: stotine mm (7 = 0.07 mm, 35 = 0.35 mm).
        var hundredths = (short)MathNet48.Clamp((int)Math.Round(mm * 100.0), 0, 211);
        try
        {
            return (LineWeight)hundredths;
        }
        catch
        {
            return LineWeight.ByLayer;
        }
    }

    [CommandMethod("TCMTERIZOLBL", CommandFlags.Modal)]
    public void LabelTerrainContours()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        try
        {
            var labeled = 0;
            while (true)
            {
                var peo = new PromptEntityOptions(
                    "\nIzaberite izohipsu za kotnu oznaku (Enter = kraj): ")
                {
                    AllowNone = true
                };
                peo.SetRejectMessage("\nSamo polilinija/spline izohipse.");
                peo.AddAllowedClass(typeof(Polyline), exactMatch: false);
                peo.AddAllowedClass(typeof(Polyline2d), exactMatch: false);
                peo.AddAllowedClass(typeof(Polyline3d), exactMatch: false);
                peo.AddAllowedClass(typeof(Spline), exactMatch: false);

                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                {
                    break;
                }

                using var tr = db.TransactionManager.StartTransaction();
                RoadDrawing.EnsureRegApp(tr, db);
                if (tr.GetObject(per.ObjectId, OpenMode.ForRead) is not Entity entity)
                {
                    tr.Commit();
                    continue;
                }

                if (!TryGetContourElevation(entity, out var elevation, out var insertAt))
                {
                    ed.WriteMessage("\nTCM-INZINJERING: Objekat nije TCM izohipsa.");
                    tr.Commit();
                    continue;
                }

                // Opciono: tačka na konturi bliža kliku.
                insertAt = ClosestPointOnContour(entity, per.PickedPoint) ?? insertAt;

                EnsureContourLayers(tr, db);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);

                var height = Math.Max(0.5, Math.Abs(elevation) * 0.002);
                if (height < 0.25)
                {
                    height = 0.5;
                }

                var text = new DBText
                {
                    Position = new Point3d(insertAt.X, insertAt.Y, elevation),
                    Height = height,
                    TextString = elevation.ToString("0.00"),
                    Layer = ContourLabelLayer
                };
                modelSpace.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                TerrainContourXData.AttachContourLabel(text, elevation);
                tr.Commit();
                labeled++;
                ed.WriteMessage($"\nTCM-INZINJERING: Kotna oznaka {elevation:0.00}.");
            }

            if (labeled > 0)
            {
                ed.WriteMessage($"\nTCM-INZINJERING: Ukupno kotnih oznaka: {labeled}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
        }
    }

    [CommandMethod("TCMTERSPOT", CommandFlags.Modal)]
    public void SpotTerrainElevation()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        try
        {
            TerrainMesh mesh;
            using (var loadTr = db.TransactionManager.StartTransaction())
            {
                var triangles = LoadTerrainTrianglesFromModel(loadTr, db);
                mesh = new TerrainMesh(triangles);
                loadTr.Commit();
            }

            if (mesh.TriangleCount == 0)
            {
                ed.WriteMessage(
                    "\nTCM-INZINJERING: Nema TCM 3DFACE terena. Prvo TCMTERFACE.");
                return;
            }

            var placed = 0;
            while (true)
            {
                var prompt = new PromptPointOptions(
                    "\nTacka za spot elevaciju (Enter = kraj): ")
                {
                    AllowNone = true
                };
                var pick = ed.GetPoint(prompt);
                if (pick.Status != PromptStatus.OK)
                {
                    break;
                }

                if (!mesh.TryGetElevation(pick.Value.X, pick.Value.Y, out var z))
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Tacka nije na terenu (van TIN XY).");
                    continue;
                }

                using var tr = db.TransactionManager.StartTransaction();
                RoadDrawing.EnsureRegApp(tr, db);
                EnsureContourLayers(tr, db);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);

                var at = new Point3d(pick.Value.X, pick.Value.Y, z);
                var mark = new DBPoint(at) { Layer = SpotLayer };
                modelSpace.AppendEntity(mark);
                tr.AddNewlyCreatedDBObject(mark, true);
                TerrainContourXData.AttachSpot(mark, z);

                var height = Math.Max(0.4, Math.Abs(z) * 0.002);
                var text = new DBText
                {
                    Position = new Point3d(at.X + height * 0.3, at.Y + height * 0.3, z),
                    Height = height,
                    TextString = z.ToString("0.00"),
                    Layer = SpotLayer
                };
                modelSpace.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                TerrainContourXData.AttachSpot(text, z);
                tr.Commit();
                placed++;
                ed.WriteMessage($"\nTCM-INZINJERING: Spot Z = {z:0.00}.");
            }

            if (placed > 0)
            {
                ed.WriteMessage($"\nTCM-INZINJERING: Ukupno spot oznaka: {placed}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
        }
    }

    private static List<TerrainTriangle> LoadTerrainTrianglesFromModel(Transaction tr, Database db)
    {
        var triangles = new List<TerrainTriangle>();
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
            triangles.Add(new TerrainTriangle(p0, p1, p2));
            if (p3.DistanceTo(p2) > 1e-9 && p3.DistanceTo(p0) > 1e-9)
            {
                triangles.Add(new TerrainTriangle(p0, p2, p3));
            }
        }

        return triangles;
    }

    private static int EraseEntitiesWithRole(Transaction tr, Database db, string role)
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

            if (!TerrainContourXData.TryReadRole(entity, out var r, out _) || r != role)
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Erase();
            count++;
        }

        return count;
    }

    private static bool TryGetContourElevation(Entity entity, out double elevation, out Point3d mid)
    {
        elevation = 0;
        mid = Point3d.Origin;

        if (TerrainContourXData.TryReadRole(entity, out var role, out elevation) &&
            role == TerrainContourXData.RoleContour)
        {
            mid = GetEntityMidpoint(entity, elevation);
            return true;
        }

        // Fallback: layer + elevation property.
        if (entity is Polyline pl &&
            (string.Equals(pl.Layer, ContourMinorLayer, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(pl.Layer, ContourMajorLayer, StringComparison.OrdinalIgnoreCase)))
        {
            elevation = pl.Elevation;
            mid = GetEntityMidpoint(pl, elevation);
            return pl.NumberOfVertices >= 2;
        }

        return false;
    }

    private static Point3d GetEntityMidpoint(Entity entity, double elevation)
    {
        if (entity is Polyline pl && pl.NumberOfVertices >= 2)
        {
            var half = pl.Length * 0.5;
            try
            {
                var p = pl.GetPointAtDist(half);
                return new Point3d(p.X, p.Y, elevation);
            }
            catch
            {
                var a = pl.GetPoint2dAt(0);
                var b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                return new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, elevation);
            }
        }

        if (entity is Curve curve)
        {
            try
            {
                var start = curve.StartParam;
                var end = curve.EndParam;
                var p = curve.GetPointAtParameter((start + end) * 0.5);
                return new Point3d(p.X, p.Y, elevation);
            }
            catch
            {
                // fall through
            }
        }

        return new Point3d(entity.GeometricExtents.MinPoint.X, entity.GeometricExtents.MinPoint.Y, elevation);
    }

    private static Point3d? ClosestPointOnContour(Entity entity, Point3d pick)
    {
        try
        {
            if (entity is Curve curve)
            {
                var p = curve.GetClosestPointTo(pick, false);
                var z = entity is Polyline pl ? pl.Elevation : elevationFromXDataOrPick(entity, p.Z);
                return new Point3d(p.X, p.Y, z);
            }
        }
        catch
        {
            // ignore
        }

        return null;

        static double elevationFromXDataOrPick(Entity e, double fallback)
        {
            return TerrainContourXData.TryReadRole(e, out _, out var elev) ? elev : fallback;
        }
    }

    private static void EnsureContourLayers(Transaction tr, Database db)
    {
        ContourPreferences.Load();
        var style = ContourPreferences.Current;
        foreach (var key in new[] { "Minor Contour", "Major Contour", "User Contours" })
        {
            var c = style.GetComponent(key);
            var layer = string.IsNullOrWhiteSpace(c.Layer) || c.Layer == "0"
                ? key switch
                {
                    "Major Contour" => ContourMajorLayer,
                    "User Contours" => "TCM_IZO_USER",
                    _ => ContourMinorLayer
                }
                : c.Layer;
            EnsureNamedLayer(tr, db, layer,
                c.ColorByLayer ? (short)7 : c.ColorAci,
                c.LineWeightByBlock ? 0 : c.LineWeightMm);
        }

        EnsureNamedLayer(tr, db, ContourLabelLayer, 2, 0);
        EnsureNamedLayer(tr, db, SpotLayer, 4, 0);
    }

    private static void EnsureNamedLayer(
        Transaction tr,
        Database db,
        string name,
        short aci,
        double lineWeightMm)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var color = AcColor.FromColorIndex(ColorMethod.ByAci, aci);
        var lw = MmToLineWeight(lineWeightMm);

        if (layerTable.Has(name))
        {
            var existing = (LayerTableRecord)tr.GetObject(layerTable[name], OpenMode.ForWrite);
            existing.Color = color;
            if (lw != LineWeight.ByLayer)
            {
                existing.LineWeight = lw;
            }

            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = color,
            LineWeight = lw
        };
        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }
}
