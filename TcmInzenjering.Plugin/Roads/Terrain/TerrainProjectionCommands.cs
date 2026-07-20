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
            if (!TryResolveTerrainForProjection(ed, db, out var terrainIds, out var terrainLabel))
            {
                ed.WriteMessage("\nTCM-ROADS: Komanda otkazana / teren nije izabran.");
                return;
            }

            var axisPick = SelectAxisEntity(ed);
            if (axisPick == ObjectId.Null)
            {
                ed.WriteMessage("\nTCM-ROADS: Komanda otkazana.");
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
                        "\nTCM-ROADS: U izabranom terenu nema validne geometrije (Face/Mesh ili Civil Tin Surface).");
                    tr.Commit();
                    return;
                }

                if (!TryResolveAxis(tr, db, axisPick, out axisName, out axis))
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Izaberite TCM osovinu (Line/Arc na TCM_OSOVINA) ili izvornu polylinu (SRCPL).");
                    tr.Commit();
                    return;
                }

                edgeCrossings = TerrainProjector.CountTerrainEdgeCrossings(axis, terrain);
                tr.Commit();
            }

            if (!TryPromptSamplingOptions(axis, edgeCrossings, out var sampling))
            {
                ed.WriteMessage("\nTCM-ROADS: Komanda otkazana.");
                return;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                terrain = TerrainMeshBuilder.Build(tr, terrainIds);
                if (!TryResolveAxis(tr, db, axisPick, out axisName, out axis))
                {
                    ed.WriteMessage("\nTCM-ROADS: Osovina nije pronadjena.");
                    tr.Commit();
                    return;
                }

                var projection = TerrainProjector.ProjectRoadAxis(axis, terrain, sampling);
                if (projection.Points.Count < 2)
                {
                    ed.WriteMessage(
                        $"\nTCM-ROADS: Projekcija nije uspela (hit {projection.HitCount}, miss {projection.MissCount}). " +
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
                    $"\nTCM-ROADS: Projekcija ose '{axisName}' na teren{(string.IsNullOrWhiteSpace(terrainLabel) ? string.Empty : $" „{terrainLabel}“")} — " +
                    $"3D polilinija sa {projection.Points.Count} temena ({modeText}; hit {projection.HitCount}, miss {projection.MissCount}).");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>
    /// Dijalog snimljenih terena ili pick jednog elementa u crtežu (vraca sve Face-ove tog modela).
    /// </summary>
    private static bool TryResolveTerrainForProjection(
        Editor ed,
        Database db,
        out IReadOnlyList<ObjectId> terrainIds,
        out string terrainLabel)
    {
        terrainIds = Array.Empty<ObjectId>();
        terrainLabel = "";

#if BRICSCAD
        terrainIds = SelectTerrainEntities(ed);
        return terrainIds.Count > 0;
#else
        while (true)
        {
            IReadOnlyList<string> names;
            string? active;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                names = NamedTerrainSurfaceStore.ListNames(tr, db);
                active = NamedTerrainSurfaceStore.GetActiveName(tr, db);
                tr.Commit();
            }

            var sourceDlg = new TerrainProjectionSourceDialog(names, active);
            var shown = AcApp.ShowModalWindow(sourceDlg) == true;

            if (!shown || sourceDlg.CloseAction == TerrainProjectionSourceCloseAction.Cancelled)
            {
                return false;
            }

            if (sourceDlg.CloseAction == TerrainProjectionSourceCloseAction.PickInDrawing)
            {
                if (!TryPickTerrainElement(ed, db, out var pickedIds, out var label))
                {
                    // Povratak na dijalog.
                    continue;
                }

                terrainIds = pickedIds;
                terrainLabel = label;
                return terrainIds.Count > 0;
            }

            // Confirmed from list.
            var selectedName = sourceDlg.SelectedTerrainName;
            if (string.IsNullOrWhiteSpace(selectedName))
            {
                continue;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                NamedTerrainSurfaceStore.ActivateSurface(tr, db, selectedName!, out _);
                terrainIds = CollectTerrainEntityIds(tr, db, selectedName);
                tr.Commit();
            }

            if (terrainIds.Count == 0)
            {
                ed.WriteMessage(
                    $"\nTCM-ROADS: Teren „{selectedName}“ je snimljen, ali u crtežu nema 3DFACE. " +
                    "Pokrenite TCMTERFACE da ga ponovo nacrtate.");
                continue;
            }

            terrainLabel = selectedName!;
            return true;
        }
#endif
    }

    private static bool TryPickTerrainElement(
        Editor ed,
        Database db,
        out IReadOnlyList<ObjectId> terrainIds,
        out string terrainLabel)
    {
        terrainIds = Array.Empty<ObjectId>();
        terrainLabel = "";

        var peo = new PromptEntityOptions(
            "\nIzaberite jedan element terena (3DFACE / border / Civil Tin Surface) <Enter = nazad>: ")
        {
            AllowNone = true
        };

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

        // Civil Tin Surface — sama selekcija je dovoljna.
        if (TinSurfaceInterop.IsTinSurface(entity))
        {
            terrainIds = new[] { per.ObjectId };
            terrainLabel = "Civil Tin Surface";
            tr.Commit();
            return true;
        }

        string? surfaceName = null;
        var isKnownTerrain =
            TerrainFaceXData.IsTerrainFace(entity) ||
            TerrainBorderXData.IsTerrainBorder(entity) ||
            TerrainContourXData.IsContour(entity) ||
            entity is Face or SubDMesh or PolyFaceMesh ||
            string.Equals(entity.Layer, TerrainLayerName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.Layer, TerrainBorderLayerName, StringComparison.OrdinalIgnoreCase);

        if (!isKnownTerrain)
        {
            ed.WriteMessage(
                "\nTCM-ROADS: To nije element terena. Kliknite 3DFACE, border ili Tin Surface (Enter = nazad na dijalog).");
            tr.Commit();
            return false;
        }

        if (TerrainFaceXData.IsTerrainFace(entity))
        {
            TerrainFaceXData.TryGetSurfaceName(entity, out surfaceName);
        }
        else if (TerrainBorderXData.IsTerrainBorder(entity))
        {
            TerrainBorderXData.TryGetSurfaceName(entity, out surfaceName);
        }
        else if (TerrainContourXData.IsContour(entity) ||
                 string.Equals(entity.Layer, TerrainLayerName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(entity.Layer, TerrainBorderLayerName, StringComparison.OrdinalIgnoreCase))
        {
            surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db);
        }
        else if (entity is SubDMesh or PolyFaceMesh)
        {
            // Samostalan mesh bez TCM markera — dovoljan je taj objekat.
            terrainIds = new[] { per.ObjectId };
            terrainLabel = entity.Layer;
            tr.Commit();
            return true;
        }

        surfaceName ??= NamedTerrainSurfaceStore.GetActiveName(tr, db);
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            NamedTerrainSurfaceStore.ActivateSurface(tr, db, surfaceName!, out _);
            terrainIds = CollectTerrainEntityIds(tr, db, surfaceName);
            terrainLabel = surfaceName!;
        }
        else
        {
            // Nema imena — svi TCM 3DFACE u crtežu.
            terrainIds = CollectTerrainEntityIds(tr, db, surfaceName: null);
            terrainLabel = "TCM teren";
        }

        tr.Commit();
        return terrainIds.Count > 0;
    }

    /// <summary>
    /// Skuplja 3DFACE (i opciono border) koji pripadaju snimljenom modelu.
    /// Jedan Face sa imenom → ceo taj TIN model.
    /// </summary>
    private static IReadOnlyList<ObjectId> CollectTerrainEntityIds(
        Transaction tr,
        Database db,
        string? surfaceName)
    {
        var ids = new List<ObjectId>();
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                continue;
            }

            if (TerrainFaceXData.IsTerrainFace(entity) ||
                (entity is Face &&
                 string.Equals(entity.Layer, TerrainLayerName, StringComparison.OrdinalIgnoreCase)))
            {
                if (MatchesSurfaceName(entity, surfaceName, isFace: true))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static bool MatchesSurfaceName(Entity entity, string? surfaceName, bool isFace)
    {
        if (string.IsNullOrWhiteSpace(surfaceName))
        {
            return true;
        }

        string? name = null;
        if (isFace)
        {
            TerrainFaceXData.TryGetSurfaceName(entity, out name);
        }
        else if (TerrainBorderXData.IsTerrainBorder(entity))
        {
            TerrainBorderXData.TryGetSurfaceName(entity, out name);
        }

        // Face bez imena (stari crtež) — uključi; novi Face-ovi imaju ime površine.
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return string.Equals(name, surfaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryPromptSamplingOptions(
        RoadAxis axis,
        int edgeCrossings,
        out TerrainSamplingOptions options)
    {
        options = new TerrainSamplingOptions();
        var structure = TerrainProjector.EstimateStructureStationCount(axis);
        var defaultCount = TerrainProjector.SuggestPointCount(axis);

#if BRICSCAD
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
            $"\nTCM-ROADS: Duzina ose {axis.TotalLength:0.##} m. " +
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
