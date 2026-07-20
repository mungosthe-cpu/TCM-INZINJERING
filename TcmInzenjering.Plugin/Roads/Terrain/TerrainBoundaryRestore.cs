using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Vraća obrisanu granicu iz snimka u crtež i ponovo gradi TIN sa granicom.</summary>
internal static class TerrainBoundaryRestore
{
    public static bool TryRestore(string boundaryKey, out string message)
    {
        message = string.Empty;
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            message = "Nema aktivnog crteza.";
            return false;
        }

        if (!TcmProjectStore.TryParseBoundaryKey(boundaryKey, out var kind, out _))
        {
            message = "Neispravan kljuc granice.";
            return false;
        }

        try
        {
            TerrainBoundaryEraseMonitor.Suppress();
            using var docLock = doc.LockDocument();
            long newHandle;
            string surfaceName;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var snap = TerrainBoundarySnapshotStore.Find(tr, doc.Database, boundaryKey);
                if (snap is null)
                {
                    message = "Nema snimka granice za vracanje u ovom crtezu.";
                    tr.Commit();
                    return false;
                }

                kind = snap.ParsedKind;
                surfaceName = snap.SurfaceName;
                var points = snap.ToPoints();
                if (points.Count < 2)
                {
                    message = "Snimak granice nema dovoljno tacaka.";
                    tr.Commit();
                    return false;
                }

                RoadDrawing.EnsureRegApp(tr, doc.Database);
                EnsureLayer(tr, doc.Database);

                var pl = new Polyline(points.Count);
                for (var i = 0; i < points.Count; i++)
                {
                    pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
                }

                pl.Closed = snap.Closed || kind == TerrainBoundaryKind.Outer;
                pl.Elevation = points[0].Z;
                pl.Layer = TerrainUserBoundaryXData.LayerName;

                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),
                    OpenMode.ForWrite);
                ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                TerrainUserBoundaryXData.Attach(pl, kind, surfaceName);

                newHandle = pl.Handle.Value;
                var list = TerrainDefinitionStore.LoadBoundaries(tr, doc.Database).ToList();
                list.RemoveAll(b => b.Handle == newHandle);
                list.Add(new TerrainBoundaryRef(newHandle, kind));
                if (kind == TerrainBoundaryKind.Outer)
                {
                    list = list.Where(b => b.Kind != TerrainBoundaryKind.Outer || b.Handle == newHandle)
                        .ToList();
                }

                TerrainDefinitionStore.SaveBoundaries(tr, doc.Database, list);
                TerrainBoundarySnapshotStore.Remove(tr, doc.Database, boundaryKey);
                tr.Commit();
            }

            var newKey = TcmProjectStore.FormatBoundaryKey(kind, newHandle);
            ReplaceBoundaryKeyInProjects(boundaryKey, newKey);

            RoadCommands.RebuildTerrainFacesPublic(doc.Editor, doc.Database, announce: true, surfaceName);
            message = $"Granica {kind} vracena (handle {newHandle}). TIN sa granicom.";
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

    private static void ReplaceBoundaryKeyInProjects(string oldKey, string newKey)
    {
        foreach (var project in TcmProjectStore.LoadAll())
        {
            project.BoundaryKeys ??= [];
            var changed = false;
            for (var i = 0; i < project.BoundaryKeys.Count; i++)
            {
                if (string.Equals(project.BoundaryKeys[i], oldKey, StringComparison.OrdinalIgnoreCase))
                {
                    project.BoundaryKeys[i] = newKey;
                    changed = true;
                }
            }

            if (!changed)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            project.BoundaryKeys = project.BoundaryKeys.Where(k => seen.Add(k)).ToList();
            TcmProjectStore.Save(project);
        }
    }

    private static void EnsureLayer(Transaction tr, Database db)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(TerrainUserBoundaryXData.LayerName))
        {
            return;
        }

        lt.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = TerrainUserBoundaryXData.LayerName,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, 1)
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }
}
