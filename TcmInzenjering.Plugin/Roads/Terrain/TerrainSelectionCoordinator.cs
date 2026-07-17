using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using TcmInzenjering.Plugin.Ribbon;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Civil Tin Surface kontekst: selekcija TCM Face / Border / Contour → ribbon tab „Tin Surface: ime“.
/// </summary>
internal static class TerrainSelectionCoordinator
{
    private static bool _idleHooked;
    private static string? _pendingSurfaceName;
    private static bool _pendingClear;

    public static void Initialize()
    {
#if !BRICSCAD
        AcApp.DocumentManager.DocumentCreated += OnDocumentCreated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            AttachDocument(doc);
        }
#endif
    }

    public static void Terminate()
    {
#if !BRICSCAD
        AcApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            DetachDocument(doc);
        }

        if (_idleHooked)
        {
            AcApp.Idle -= OnIdle;
            _idleHooked = false;
        }
#endif
    }

#if !BRICSCAD
    private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) =>
        AttachDocument(e.Document);

    private static void AttachDocument(Document doc)
    {
        DetachDocument(doc);
        doc.ImpliedSelectionChanged += OnImpliedSelectionChanged;
    }

    private static void DetachDocument(Document doc)
    {
        doc.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
    }

    private static void OnImpliedSelectionChanged(object sender, EventArgs e)
    {
        if (TerrainCommandGuard.IsSuppressed)
        {
            return;
        }

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !string.IsNullOrEmpty(doc.CommandInProgress))
        {
            return;
        }

        try
        {
            var sel = doc.Editor.SelectImplied();
            if (sel.Status != PromptStatus.OK || sel.Value is null || sel.Value.Count == 0)
            {
                _pendingClear = true;
                _pendingSurfaceName = null;
                EnsureIdle();
                return;
            }

            string? surfaceName = null;
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    if (so.ObjectId.IsNull || so.ObjectId.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is not Entity entity)
                    {
                        continue;
                    }

                    if (TryResolveSurfaceName(tr, doc.Database, entity, out var name))
                    {
                        surfaceName = name;
                        break;
                    }
                }

                tr.Commit();
            }

            if (string.IsNullOrWhiteSpace(surfaceName))
            {
                _pendingClear = true;
                _pendingSurfaceName = null;
            }
            else
            {
                _pendingClear = false;
                _pendingSurfaceName = surfaceName;
            }

            EnsureIdle();
        }
        catch
        {
            // ignore selection quirks
        }
    }

    private static bool TryResolveSurfaceName(
        Transaction tr,
        Database db,
        Entity entity,
        out string surfaceName)
    {
        surfaceName = "";

        if (TerrainFaceXData.IsTerrainFace(entity))
        {
            if (TerrainFaceXData.TryGetSurfaceName(entity, out var fromFace) &&
                !string.IsNullOrWhiteSpace(fromFace))
            {
                surfaceName = fromFace!;
                return true;
            }

            surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db) ?? "Surface";
            return true;
        }

        if (TerrainBorderXData.IsTerrainBorder(entity))
        {
            if (TerrainBorderXData.TryGetSurfaceName(entity, out var fromBorder) &&
                !string.IsNullOrWhiteSpace(fromBorder))
            {
                surfaceName = fromBorder!;
                return true;
            }

            surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db) ?? "Surface";
            return true;
        }

        if (TerrainContourXData.IsContour(entity))
        {
            if (TerrainContourXData.TryGetSurfaceName(entity, out var fromContour) &&
                !string.IsNullOrWhiteSpace(fromContour))
            {
                surfaceName = fromContour!;
                return true;
            }

            surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db) ?? "Surface";
            return true;
        }

        // Layer fallback: TCM teren entitija (uključujući TCM_TEREN_Teren_1).
        if (TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.TerrainLayerName) ||
            TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.TerrainBorderLayerName) ||
            TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.ContourMajorLayer) ||
            TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.ContourMinorLayer) ||
            TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, "TCM_IZO_USER") ||
            TerrainLayerNames.IsBaseOrPrefixed(entity.Layer, RoadCommands.ContourLabelLayer))
        {
            // Izvuci ime iz sufiksa lejera ako postoji.
            var layer = entity.Layer ?? "";
            foreach (var prefix in new[]
                     {
                         RoadCommands.TerrainLayerName + "_",
                         RoadCommands.TerrainBorderLayerName + "_",
                         RoadCommands.ContourMajorLayer + "_",
                         RoadCommands.ContourMinorLayer + "_",
                         "TCM_IZO_USER_",
                         RoadCommands.ContourLabelLayer + "_"
                     })
            {
                if (layer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    layer.Length > prefix.Length)
                {
                    surfaceName = layer[prefix.Length..];
                    return true;
                }
            }

            surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db) ?? "Surface";
            return true;
        }

        return false;
    }

    private static void EnsureIdle()
    {
        if (_idleHooked)
        {
            return;
        }

        AcApp.Idle += OnIdle;
        _idleHooked = true;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        AcApp.Idle -= OnIdle;
        _idleHooked = false;

        if (TerrainCommandGuard.IsSuppressed)
        {
            return;
        }

        try
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            // Tokom aktivne komande (npr. TCMTERIZOLBL petlja) NE otvaraj transakciju.
            if (doc is not null && !string.IsNullOrEmpty(doc.CommandInProgress))
            {
                if (!_pendingClear && !string.IsNullOrWhiteSpace(_pendingSurfaceName))
                {
                    RibbonBuilder.ShowTinSurfaceTab(_pendingSurfaceName!);
                }

                return;
            }

            if (_pendingClear || string.IsNullOrWhiteSpace(_pendingSurfaceName))
            {
                RibbonBuilder.HideTinSurfaceTab();
                return;
            }

            RibbonBuilder.ShowTinSurfaceTab(_pendingSurfaceName!);

            if (doc is null)
            {
                return;
            }

            // Samo ribbon + aktivacija — bez LockDocument (već smo van komande).
            Transaction? tr = null;
            try
            {
                tr = doc.Database.TransactionManager.StartTransaction();
                NamedTerrainSurfaceStore.ActivateSurface(tr, doc.Database, _pendingSurfaceName!, out _);
                tr.Commit();
                tr = null;
            }
            catch
            {
                try
                {
                    tr?.Abort();
                }
                catch
                {
                    // ignore
                }
            }
            finally
            {
                try
                {
                    tr?.Dispose();
                }
                catch
                {
                    // eInvalidContext na Dispose
                }
            }
        }
        catch
        {
            // ribbon nije spreman
        }
    }
#endif
}
