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
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
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
            surfaceName = NamedTerrainSurfaceStore.GetActiveName(tr, db) ?? "Surface";
            return true;
        }

        // Layer fallback: TCM teren entitija.
        if (string.Equals(entity.Layer, RoadCommands.TerrainLayerName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.Layer, "TCM_TER_BOUND", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.Layer, RoadCommands.ContourMajorLayer, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.Layer, RoadCommands.ContourMinorLayer, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.Layer, "TCM_IZO_USER", StringComparison.OrdinalIgnoreCase))
        {
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

        try
        {
            if (_pendingClear || string.IsNullOrWhiteSpace(_pendingSurfaceName))
            {
                RibbonBuilder.HideTinSurfaceTab();
                return;
            }

            RibbonBuilder.ShowTinSurfaceTab(_pendingSurfaceName!);
        }
        catch
        {
            // ribbon nije spreman
        }
    }
#endif
}
