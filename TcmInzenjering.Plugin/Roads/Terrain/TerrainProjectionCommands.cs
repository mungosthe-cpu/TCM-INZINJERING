using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    [CommandMethod("TCMPROJTER", CommandFlags.Modal)]
    public void ProjectAxisToTerrain()
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
            var terrainIds = SelectTerrainEntities(ed);
            if (terrainIds.Count == 0)
            {
                ed.WriteMessage("\nTCM-INZINJERING: Nije izabran 3D teren (3DFACE / Mesh / Tin Surface).");
                return;
            }

            var axisPick = SelectAxisEntity(ed);
            if (axisPick == ObjectId.Null)
            {
                ed.WriteMessage("\nTCM-INZINJERING: Komanda otkazana.");
                return;
            }

            TerrainElevationModel terrain;
            string axisName;
            RoadAxis axis;
            int edgeCrossings;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                terrain = TerrainMeshBuilder.Build(tr, terrainIds);
                if (!terrain.HasTerrain)
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: U selekciji nema validnog terena (Face/Mesh ili Civil Tin Surface).");
                    tr.Commit();
                    return;
                }

                if (!TryResolveAxis(tr, db, axisPick, out axisName, out axis))
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Izaberite TCM osovinu (Line/Arc na TCM_OSOVINA) ili izvornu polylinu (SRCPL).");
                    tr.Commit();
                    return;
                }

                edgeCrossings = TerrainProjector.CountTerrainEdgeCrossings(axis, terrain);
                tr.Commit();
            }

            if (!TryPromptSamplingOptions(axis, edgeCrossings, out var sampling))
            {
                ed.WriteMessage("\nTCM-INZINJERING: Komanda otkazana.");
                return;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                terrain = TerrainMeshBuilder.Build(tr, terrainIds);
                if (!TryResolveAxis(tr, db, axisPick, out axisName, out axis))
                {
                    ed.WriteMessage("\nTCM-INZINJERING: Osovina nije pronadjena.");
                    tr.Commit();
                    return;
                }

                var projection = TerrainProjector.ProjectRoadAxis(axis, terrain, sampling);
                if (projection.Points.Count < 2)
                {
                    ed.WriteMessage(
                        $"\nTCM-INZINJERING: Projekcija nije uspela (hit {projection.HitCount}, miss {projection.MissCount}). " +
                        "Proveri da li osovina leži iznad terena u XY.");
                    tr.Commit();
                    return;
                }

                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);
                TerrainProjectionRefresh.DeleteProjectedAxes(tr, db, axisName);
                var id = RoadDrawing.DrawProjectedAxis(tr, modelSpace, axisName, projection.Points);
                if (!id.IsNull)
                {
                    RoadDrawing.SendProjectedAxisBelowPickables(tr, db, id, axisName);
                }

                TerrainProjectionStore.Save(
                    tr,
                    db,
                    axisName,
                    sampling,
                    axis.TotalLength,
                    terrainIds);
                tr.Commit();

                var modeText = sampling.Mode == TerrainSamplingMode.TerrainEdgeCrossings
                    ? $"preseci ({projection.EdgeCrossingCount}) + preciznost {sampling.PointCount}"
                    : $"preciznost {sampling.PointCount} tacaka";

                ed.WriteMessage(
                    $"\nTCM-INZINJERING: Projekcija ose '{axisName}' na teren — " +
                    $"3D polilinija sa {projection.Points.Count} temena ({modeText}; hit {projection.HitCount}, miss {projection.MissCount}). " +
                    $"Handle={(id.IsNull ? "?" : id.Handle)}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
        }
    }

    private static bool TryPromptSamplingOptions(
        RoadAxis axis,
        int edgeCrossings,
        out TerrainSamplingOptions options)
    {
        options = new TerrainSamplingOptions();
        var structure = TerrainProjector.EstimateStructureStationCount(axis);
        var defaultCount = TerrainProjector.SuggestPointCount(axis);

#if NETFRAMEWORK || BRICSCAD
        return TryPromptSamplingOptionsCli(axis, edgeCrossings, structure, defaultCount, out options);
#else
        try
        {
            var dialog = new TerrainProjectionDialog(
                axis.TotalLength,
                edgeCrossings,
                structure,
                defaultCount);
            if (AcApp.ShowModalWindow(dialog) != true)
            {
                return false;
            }

            options = dialog.ToOptions();
            return true;
        }
        catch
        {
            return TryPromptSamplingOptionsCli(axis, edgeCrossings, structure, defaultCount, out options);
        }
#endif
    }

    private static bool TryPromptSamplingOptionsCli(
        RoadAxis axis,
        int edgeCrossings,
        int structure,
        int defaultCount,
        out TerrainSamplingOptions options)
    {
        options = new TerrainSamplingOptions();
        var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
        {
            return false;
        }

        var estimated = Math.Max(2, structure + edgeCrossings);
        ed.WriteMessage(
            $"\nTCM-INZINJERING: Duzina ose {axis.TotalLength:0.##} m. " +
            $"Pronadjeno {edgeCrossings} preseka (~{estimated} kljucnih temena).");

        var keywordOpts = new PromptKeywordOptions(
            "\nNacin podele 3D ose [Preseci/Broj] <Preseci>: ")
        {
            AllowNone = true
        };
        keywordOpts.Keywords.Add("Preseci");
        keywordOpts.Keywords.Add("Broj");
        keywordOpts.Keywords.Default = "Preseci";

        var keywordResult = ed.GetKeywords(keywordOpts);
        if (keywordResult.Status == PromptStatus.Cancel)
        {
            return false;
        }

        var useBroj = keywordResult.Status == PromptStatus.OK &&
                      string.Equals(keywordResult.StringResult, "Broj", StringComparison.OrdinalIgnoreCase);

        if (!useBroj)
        {
            options = new TerrainSamplingOptions { Mode = TerrainSamplingMode.TerrainEdgeCrossings };
            return true;
        }

        var countOpts = new PromptIntegerOptions(
            $"\nUkupan broj 3D tacaka na osi <{defaultCount}>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = defaultCount,
            UseDefaultValue = true,
            LowerLimit = 2,
            UpperLimit = 100000
        };

        var countResult = ed.GetInteger(countOpts);
        if (countResult.Status == PromptStatus.Cancel)
        {
            return false;
        }

        var count = countResult.Status == PromptStatus.OK ? countResult.Value : defaultCount;
        options = new TerrainSamplingOptions
        {
            Mode = TerrainSamplingMode.FixedPointCount,
            PointCount = Math.Max(2, count)
        };
        return true;
    }

    private static IReadOnlyList<ObjectId> SelectTerrainEntities(Editor ed)
    {
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nIzaberite 3D teren (3DFACE / Mesh / Tin Surface): "
        };

        var filter = new SelectionFilter(
        [
            new TypedValue(
                (int)DxfCode.Start,
                "3DFACE,POLYFACE,MESH,SUBDMESH,AECC_TIN_SURFACE,AECC_GRID_SURFACE")
        ]);

        var result = ed.GetSelection(options, filter);
        if (result.Status != PromptStatus.OK)
        {
            result = ed.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nIzaberite 3D teren objekte (Face/Mesh/Tin Surface): "
            });
            if (result.Status != PromptStatus.OK)
            {
                return Array.Empty<ObjectId>();
            }
        }

        return result.Value.GetObjectIds();
    }

    private static ObjectId SelectAxisEntity(Editor ed)
    {
        var options = new PromptEntityOptions(
            "\nIzaberite osovinu (TCM Line/Arc ili izvornu polylinu): ")
        {
            AllowNone = false
        };
        options.SetRejectMessage("\nIzaberite Line, Arc ili LWPOLYLINE.");
        options.AddAllowedClass(typeof(Line), exactMatch: false);
        options.AddAllowedClass(typeof(Arc), exactMatch: false);
        options.AddAllowedClass(typeof(Polyline), exactMatch: false);
        var result = ed.GetEntity(options);
        return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
    }

    private static bool TryResolveAxis(
        Transaction tr,
        Database db,
        ObjectId entityId,
        out string axisName,
        out RoadAxis axis)
    {
        axisName = string.Empty;
        axis = null!;
        var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
        if (entity is null)
        {
            return false;
        }

        if (RoadXData.TryReadSourcePolyline(entity, out axisName) ||
            RoadXData.TryReadAxisElement(entity, out axisName, out _))
        {
            var metadata = RoadAxisStore.Load(tr, db, axisName);
            var startStation = metadata?.StartStation ?? 0;
            var loaded = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
            if (loaded is null || loaded.Elements.Count == 0)
            {
                return false;
            }

            axis = loaded;
            return true;
        }

        return false;
    }
}
