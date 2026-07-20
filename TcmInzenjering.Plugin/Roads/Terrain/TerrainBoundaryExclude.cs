using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Izuzima granicu iz crteža (ostaje u projektu sa !), snima geometriju i vraća TIN bez granice.
/// </summary>
internal static class TerrainBoundaryExclude
{
    public static bool TryExclude(string boundaryKey, out string message)
    {
        message = string.Empty;
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            message = "Nema aktivnog crteza.";
            return false;
        }

        if (!TcmProjectStore.TryParseBoundaryKey(boundaryKey, out var kind, out var handle))
        {
            message = "Neispravan kljuc granice.";
            return false;
        }

        try
        {
            TerrainBoundaryEraseMonitor.Suppress();
            using var docLock = doc.LockDocument();
            string surfaceName;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                // Vec izuzeta?
                if (TerrainBoundarySnapshotStore.Has(tr, doc.Database, boundaryKey) &&
                    !TerrainDefinitionStore.LoadBoundaries(tr, doc.Database).Any(b => b.Handle == handle))
                {
                    message = "Granica je vec izuzeta iz crteza.";
                    tr.Commit();
                    return false;
                }

                ObjectId? entityId = null;
                try
                {
                    entityId = doc.Database.GetObjectId(false, new Handle(handle), 0);
                }
                catch
                {
                    // ignore
                }

                if (entityId is null || entityId.Value.IsNull || entityId.Value.IsErased)
                {
                    message = "Granica nije pronadjena u crtezu.";
                    tr.Commit();
                    return false;
                }

                if (tr.GetObject(entityId.Value, OpenMode.ForWrite) is not Entity entity || entity.IsErased)
                {
                    message = "Granica nije pronadjena u crtezu.";
                    tr.Commit();
                    return false;
                }

                if (TerrainUserBoundaryXData.TryRead(entity, out var xdKind, out var xdSurface))
                {
                    kind = xdKind;
                    surfaceName = xdSurface ?? NamedTerrainSurfaceStore.GetActiveName(tr, doc.Database) ?? "Teren_1";
                }
                else
                {
                    surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, doc.Database) ?? "Teren_1";
                }

                if (NamedTerrainSurfaceStore.IsBoundaryCompanionName(surfaceName))
                {
                    surfaceName = surfaceName[..^("_Granica".Length)];
                }

                if (!TerrainBoundarySnapshotStore.TryCaptureEntity(entity, out var points, out var closed) ||
                    points.Count < 2)
                {
                    message = "Ne mogu da snimim geometriju granice.";
                    tr.Commit();
                    return false;
                }

                var key = TcmProjectStore.FormatBoundaryKey(kind, handle);
                TerrainBoundarySnapshotStore.Upsert(
                    tr,
                    doc.Database,
                    TerrainBoundarySnapshot.From(key, kind, surfaceName, closed, points));

                var list = TerrainDefinitionStore.LoadBoundaries(tr, doc.Database).ToList();
                list.RemoveAll(b => b.Handle == handle);
                TerrainDefinitionStore.SaveBoundaries(tr, doc.Database, list);

                entity.Erase();

                var granicaName = NamedTerrainSurfaceStore.BoundaryCompanionName(surfaceName);
                NamedTerrainSurfaceStore.DeleteSurface(tr, doc.Database, granicaName);
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),
                    OpenMode.ForWrite);
                TerrainBoundaryPointDrawer.Sync(tr, doc.Database, ms, granicaName, Array.Empty<Point3d>());
                tr.Commit();
            }

            RoadCommands.RebuildTerrainFacesPublic(doc.Editor, doc.Database, announce: true, surfaceName);
            message = $"Granica {kind} izuzeta iz crteza (u projektu ostaje sa !). TIN bez granice.";
            doc.Editor.WriteMessage($"\nTCM-ROADS: {message}");
            return true;
        }
        catch (System.Exception ex)
        {
            message = ex.Message;
            return false;
        }
        finally
        {
            TerrainBoundaryEraseMonitor.Resume();
        }
    }
}
