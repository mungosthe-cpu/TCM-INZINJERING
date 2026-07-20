using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Roads.CrossAxis;
using TcmInzenjering.Plugin.Roads.CrossSection;
using TcmInzenjering.Plugin.Roads.Profile;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>Poprečni profili (section views) iz pop. osa + teren (+ niveleta).</summary>
public sealed partial class RoadCommands
{
    [CommandMethod("TCMPOPPRF", CommandFlags.Modal)]
    public void DrawCrossSectionViews()
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
            if (!TryPickAxis(ed, db, out var axisName, out var axis))
            {
                return;
            }

            List<CrossAxisInfo> crossAxes;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                crossAxes = CrossAxisScanner.Scan(tr, db)
                    .Where(c => string.Equals(c.RoadAxisName, axisName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.Station)
                    .ThenBy(c => c.Number)
                    .ToList();
                tr.Commit();
            }

            if (crossAxes.Count == 0)
            {
                ed.WriteMessage("\nTCM-ROADS: Nema poprecnih osa za ovu osovinu. Prvo TCMPOPSTAC.");
                return;
            }

            if (!TryResolveTerrainForSections(ed, db, out var terrainIds))
            {
                ed.WriteMessage("\nTCM-ROADS: Nije izabran teren (3DFACE / TIN).");
                return;
            }

            var hOpt = new PromptDoubleOptions("\nHorizontalna razmera (m crteza / m offseta) <1.0>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                AllowNone = true,
                DefaultValue = 1.0
            };
            var hRes = ed.GetDouble(hOpt);
            if (hRes.Status == PromptStatus.Cancel)
            {
                return;
            }

            var hScale = hRes.Status == PromptStatus.OK ? hRes.Value : 1.0;

            var vOpt = new PromptDoubleOptions("\nVertikalna razmera (m crteza / m kote) <10.0>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                AllowNone = true,
                DefaultValue = 10.0
            };
            var vRes = ed.GetDouble(vOpt);
            if (vRes.Status == PromptStatus.Cancel)
            {
                return;
            }

            var vScale = vRes.Status == PromptStatus.OK ? vRes.Value : 10.0;

            var insert = ed.GetPoint(new PromptPointOptions(
                "\nUnosna tacka (donji levi ugao prvog poprecnog profila): "));
            if (insert.Status != PromptStatus.OK)
            {
                return;
            }

            using var drawTr = db.TransactionManager.StartTransaction();
            var terrain = TerrainMeshBuilder.Build(drawTr, terrainIds);
            if (!terrain.HasTerrain)
            {
                ed.WriteMessage("\nTCM-ROADS: Teren nema trouglova / TinSurface.");
                drawTr.Commit();
                return;
            }

            double defLeft = 3.5, defRight = 3.5;
            ProfileLaneWidthStore.TryGetDefaults(drawTr, db, axisName, out defLeft, out defRight);
            if (defLeft < 1e-6)
            {
                defLeft = StationFontPreferences.CrossAxisLeftLength;
            }

            if (defRight < 1e-6)
            {
                defRight = StationFontPreferences.CrossAxisRightLength;
            }

            var modelSpace = (BlockTableRecord)drawTr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            var batchId = Guid.NewGuid().ToString("N")[..8];
            var cursor = insert.Value;
            var drawn = 0;
            const int sampleCount = 41;

            foreach (var cx in crossAxes)
            {
                var meta = CrossAxisMetaStore.Load(drawTr, db, cx.Handle);
                var station = cx.Station > 1e-9 ? cx.Station : (meta?.Station ?? 0);
                ProfileLaneWidthStore.TryGetAtStation(
                    drawTr, db, axisName, station, out var stationLeft, out var stationRight);
                if (stationLeft < 1e-6)
                {
                    stationLeft = defLeft;
                }

                if (stationRight < 1e-6)
                {
                    stationRight = defRight;
                }

                var left = meta?.LeftWidth > 1e-6 ? meta.LeftWidth : stationLeft;
                var right = meta?.RightWidth > 1e-6 ? meta.RightWidth : stationRight;

                var center = axis.GetPointAtStation(station);
                var dir = axis.GetDirectionAtStation(station);
                if (center is null || dir is null || dir.Value.Length < 1e-9)
                {
                    continue;
                }

                var leftNormal = new Vector3d(-dir.Value.Y, dir.Value.X, 0);
                if (leftNormal.Length < 1e-9)
                {
                    continue;
                }

                leftNormal = leftNormal.GetNormal();

                var samples = SampleCrossTerrain(terrain, center.Value, leftNormal, left, right, sampleCount);
                if (samples.Count < 2)
                {
                    continue;
                }

                var minZ = samples.Min(s => s.Elevation);
                var maxZ = samples.Max(s => s.Elevation);
                double? gradeZ = null;
                var pvis = VerticalProfileStore.Load(drawTr, db, axisName);
                if (pvis.Count >= 2)
                {
                    gradeZ = VerticalProfileStore.ElevationAt(pvis, station);
                    if (gradeZ is not null)
                    {
                        minZ = Math.Min(minZ, gradeZ.Value);
                        maxZ = Math.Max(maxZ, gradeZ.Value);
                    }
                }

                var baseElev = Math.Floor(minZ - 0.5);
                var topElev = Math.Ceiling(maxZ + 0.5);
                var title = $"STA {cx.Number}  {ChainageFormatter.Format(station, ChainageFormatter.DefaultFormat)}";
                var sectionId = $"{batchId}_{cx.Number}";

                CrossSectionDrawing.DrawSection(
                    drawTr,
                    modelSpace,
                    cursor,
                    sectionId,
                    title,
                    samples,
                    gradeZ,
                    left,
                    right,
                    baseElev,
                    topElev,
                    hScale,
                    vScale);

                var sectionHeight = Math.Max(1.0, (topElev - baseElev) * vScale);
                cursor = new Point3d(cursor.X, cursor.Y + sectionHeight + 8.0, 0);
                drawn++;
            }

            drawTr.Commit();
            ed.WriteMessage(
                drawn == 0
                    ? "\nTCM-ROADS: Nijedan poprecni profil nije nacrtan (nema pogodaka na terenu)."
                    : $"\nTCM-ROADS: Nacrtano {drawn} poprecnih profila za '{axisName}'.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static List<(double Offset, double Elevation)> SampleCrossTerrain(
        TerrainElevationModel terrain,
        Point3d center,
        Vector3d leftNormal,
        double leftWidth,
        double rightWidth,
        int sampleCount)
    {
        var list = new List<(double, double)>(sampleCount);
        var total = leftWidth + rightWidth;
        if (total < 1e-6 || sampleCount < 2)
        {
            return list;
        }

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)(sampleCount - 1);
            var offset = -leftWidth + t * total;
            var p = center + leftNormal * offset;
            if (terrain.TryGetElevation(p.X, p.Y, out var z))
            {
                list.Add((offset, z));
            }
        }

        return list;
    }

    private static bool TryResolveTerrainForSections(
        Editor ed,
        Database db,
        out IReadOnlyList<ObjectId> terrainIds)
    {
        terrainIds = Array.Empty<ObjectId>();
        var peo = new PromptEntityOptions(
            "\nIzaberite teren (3DFACE / border / Tin Surface): ")
        {
            AllowNone = false
        };
        peo.SetRejectMessage("\nIzaberite element terena.");
        peo.AddAllowedClass(typeof(Face), exactMatch: false);
        peo.AddAllowedClass(typeof(Polyline), exactMatch: false);
        peo.AddAllowedClass(typeof(SubDMesh), exactMatch: false);
        peo.AddAllowedClass(typeof(PolyFaceMesh), exactMatch: false);
        peo.AddAllowedClass(typeof(Entity), exactMatch: false);

        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
        {
            return false;
        }

        using var tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(per.ObjectId, OpenMode.ForRead) is not Entity entity)
        {
            tr.Commit();
            return false;
        }

        string? surfaceName = null;
        if (TinSurfaceInterop.IsTinSurface(entity))
        {
            terrainIds = new[] { per.ObjectId };
            tr.Commit();
            return true;
        }

        if (TerrainFaceXData.IsTerrainFace(entity))
        {
            TerrainFaceXData.TryGetSurfaceName(entity, out surfaceName);
        }
        else if (TerrainBorderXData.IsTerrainBorder(entity))
        {
            TerrainBorderXData.TryGetSurfaceName(entity, out surfaceName);
        }

        surfaceName ??= NamedTerrainSurfaceStore.GetActiveName(tr, db);
        var ids = new List<ObjectId>();
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e || e.IsErased)
            {
                continue;
            }

            if (TinSurfaceInterop.IsTinSurface(e))
            {
                ids.Add(id);
                continue;
            }

            if (!TerrainFaceXData.IsTerrainFace(e) &&
                !(e is Face && string.Equals(e.Layer, TerrainLayerName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(surfaceName))
            {
                ids.Add(id);
                continue;
            }

            TerrainFaceXData.TryGetSurfaceName(e, out var name);
            if (string.Equals(name, surfaceName, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }

        // Ako nema imena — bar kliknuti objekat.
        if (ids.Count == 0 && entity is Face or SubDMesh or PolyFaceMesh)
        {
            ids.Add(per.ObjectId);
        }

        terrainIds = ids;
        tr.Commit();
        return terrainIds.Count > 0;
    }
}
