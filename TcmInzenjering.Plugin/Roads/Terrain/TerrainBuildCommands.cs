using System.Globalization;
using System.Windows;
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
/// TEREN modul: izbor 3D tačaka → Delaunay TIN → 3DFACE (kao Civil Surface from Points).
/// Swap ivice kao Civil 3D Swap Edge.
/// </summary>
public sealed partial class RoadCommands
{
    public const string TerrainLayerName = "TCM_TEREN";
    public const string TerrainBorderLayerName = "TCM_TER_BOUND";

    /// <summary>Civil 3D: klik mora biti unutar 1 jedinice od ivice.</summary>
    private const double SwapEdgePickTolerance = 1.0;

    private enum TerrainPointsMode
    {
        /// <summary>Pita Dodaj/Zameni pa bira tacke (stara TCMTERTACKE).</summary>
        InteractiveAsk,

        /// <summary>Zamenjuje skup novim izborom iz crteza.</summary>
        SelectReplace,

        /// <summary>Dodaje tacke u postojeci skup.</summary>
        SelectAppend,

        /// <summary>Ucitava XYZ CSV/TXT u crtez.</summary>
        LoadFile,

        /// <summary>Otvara dijalog nad sacuvanim tackama.</summary>
        EditExisting,

        /// <summary>Snima CSV u folder projekta.</summary>
        SaveFile
    }

    /// <summary>Kompatibilnost / glavno dugme — pita Dodaj ili Zameni.</summary>
    [CommandMethod("TCMTERTACKE", CommandFlags.Modal)]
    public void SelectTerrainPoints() => RunTerrainPointsWorkflow(TerrainPointsMode.InteractiveAsk);

    [CommandMethod("TCMTERIZABERI", CommandFlags.Modal)]
    public void SelectTerrainPointsReplace() => RunTerrainPointsWorkflow(TerrainPointsMode.SelectReplace);

    [CommandMethod("TCMTERDODAJ", CommandFlags.Modal)]
    public void SelectTerrainPointsAppend() => RunTerrainPointsWorkflow(TerrainPointsMode.SelectAppend);

    [CommandMethod("TCMTERUCITAJ", CommandFlags.Modal)]
    public void LoadTerrainPointsFile() => RunTerrainPointsWorkflow(TerrainPointsMode.LoadFile);

    [CommandMethod("TCMTERUREDI", CommandFlags.Modal)]
    public void EditTerrainPoints() => RunTerrainPointsWorkflow(TerrainPointsMode.EditExisting);

    [CommandMethod("TCMTERSNIMI", CommandFlags.Modal)]
    public void SaveTerrainPointsFile() => RunTerrainPointsWorkflow(TerrainPointsMode.SaveFile);

    /// <summary>Dodaj tacke iz istih blokova — Z iz atributa, XY iz inserta ili pozicije atributa.</summary>
    [CommandMethod("TCMTERBLOK", CommandFlags.Modal)]
    public void AddTerrainPointsFromBlock()
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
            AddTerrainPointsFromBlockCore(ed, db);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>
    /// Uzorak kruga + oblast: svi krugovi na istom lejeru unutar oblasti
    /// postaju tacke terena (XYZ centra) i snimaju se u imenovanu grupu.
    /// </summary>
    [CommandMethod("TCMTERKRUGTAC", CommandFlags.Modal)]
    public void ConvertCirclesToTerrainPoints()
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
            var sampleOptions = new PromptEntityOptions(
                "\nIzaberite jedan krug kao uzorak (traze se krugovi na istom lejeru): ")
            {
                AllowNone = false
            };
            sampleOptions.SetRejectMessage("\nPotreban je CIRCLE objekat.");
            sampleOptions.AddAllowedClass(typeof(Circle), exactMatch: true);
            var sampleResult = ed.GetEntity(sampleOptions);
            if (sampleResult.Status != PromptStatus.OK)
            {
                return;
            }

            string sourceLayer;
            using (var sampleTr = db.TransactionManager.StartTransaction())
            {
                if (sampleTr.GetObject(sampleResult.ObjectId, OpenMode.ForRead) is not Circle sample ||
                    sample.IsErased)
                {
                    ed.WriteMessage("\nTCM-ROADS: Izabrani krug nije validan.");
                    sampleTr.Commit();
                    return;
                }

                sourceLayer = sample.Layer;
                sampleTr.Commit();
            }

            var first = ed.GetPoint(
                $"\nPrvi ugao oblasti za krugove sa lejera „{sourceLayer}“: ");
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var second = ed.GetCorner(new PromptCornerOptions(
                "\nSuprotni ugao oblasti: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            var filter = new SelectionFilter(
            [
                new TypedValue((int)DxfCode.Start, "CIRCLE"),
                new TypedValue((int)DxfCode.LayerName, sourceLayer)
            ]);
            var selection = ed.SelectCrossingWindow(first.Value, second.Value, filter);
            if (selection.Status != PromptStatus.OK ||
                selection.Value is null ||
                selection.Value.Count == 0)
            {
                ed.WriteMessage(
                    $"\nTCM-ROADS: U oblasti nema krugova na lejeru „{sourceLayer}“.");
                return;
            }

            var converted = 0;
            var added = 0;
            var total = 0;
            var hadFaces = false;
            string? activeSurface = null;
            var groupName = string.Empty;
            var groupPoints = new List<Point3d>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var collected = new List<TerrainPointVm>();
                var circles = new List<Circle>();
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected is null ||
                        tr.GetObject(selected.ObjectId, OpenMode.ForWrite) is not Circle circle ||
                        circle.IsErased)
                    {
                        continue;
                    }

                    var center = circle.Center;
                    collected.Add(new TerrainPointVm(
                        center.X, center.Y, center.Z, isAdded: true));
                    if (!groupPoints.Any(p =>
                            Math.Abs(p.X - center.X) <= 1e-8 &&
                            Math.Abs(p.Y - center.Y) <= 1e-8))
                    {
                        groupPoints.Add(center);
                    }
                    circles.Add(circle);
                }

                if (collected.Count == 0)
                {
                    ed.WriteMessage("\nTCM-ROADS: Nije pronadjen nijedan validan krug.");
                    tr.Commit();
                    return;
                }

                var working = LoadStoredAsVm(tr, db);
                var before = working.Count;
                MergeCollected(working, collected);
                PersistAndSyncPoints(tr, db, working, eraseHandles: null);

                groupName = TerrainPointGroupStore.GetNextDefaultName(tr, db);
                TerrainPointGroupStore.Save(tr, db, groupName, groupPoints);

                activeSurface = NamedTerrainSurfaceStore.GetActiveName(tr, db);
                if (!string.IsNullOrWhiteSpace(activeSurface))
                {
                    NamedTerrainSurfaceStore.SaveSurface(
                        tr, db, activeSurface, working.Select(p => p.ToPoint3d()).ToList());
                }

                // Brisanje je deo iste AutoCAD transakcije, pa standardni UNDO vraca
                // i krugove i prethodno stanje tacaka.
                foreach (var circle in circles)
                {
                    circle.Erase();
                    converted++;
                }

                added = Math.Max(0, working.Count - before);
                total = working.Count;
                hadFaces = CountTerrainFaces(tr, db) > 0;
                tr.Commit();
            }

            ed.WriteMessage(
                $"\nTCM-ROADS: Pretvoreno {converted} krugova u tacke terena " +
                $"(novih {added}, ukupno {total}). XYZ je preuzet iz centra svakog kruga.");

            if (total >= 3 && (hadFaces || !string.IsNullOrWhiteSpace(activeSurface)))
            {
                RebuildTerrainFaces(ed, db, announce: true, activeSurface);
            }

            ed.Regen();

#if !BRICSCAD
            var report = new CirclePointConversionSummaryDialog(
                sourceLayer, groupName, groupPoints, converted, added);
            if (AcApp.ShowModalWindow(report) == true && report.SaveRequested)
            {
                var saveName = report.GroupName;
                if (!string.Equals(saveName, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    using var renameTr = db.TransactionManager.StartTransaction();
                    TerrainPointGroupStore.Rename(
                        renameTr, db, groupName, saveName, groupPoints);
                    renameTr.Commit();
                    groupName = saveName;
                }

                // Tacke su vec u crtezu; ovde snimamo imenovani skup + projekat
                // (bez zamene celog TerrainPointStore samo grupom).
                string saveResult;
                try
                {
                    var normalized = NamedTerrainSurfaceStore.NormalizeName(saveName);
                    using (var saveTr = db.TransactionManager.StartTransaction())
                    {
                        NamedTerrainSurfaceStore.SaveSurface(
                            saveTr, db, normalized, groupPoints, setActive: true);
                        saveTr.Commit();
                    }

                    var projectError = TcmProjectStore.AddPointSetToActiveProject(normalized);
                    saveResult = projectError is null
                        ? $"Skup tacaka „{normalized}“ snimljen u crtez i aktivni TCM projekat " +
                          $"({groupPoints.Count} tacaka)."
                        : "ERR:" + projectError;
                }
                catch (System.Exception ex)
                {
                    saveResult = "ERR:" + ex.Message;
                }

                if (saveResult.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    ed.WriteMessage($"\nTCM-ROADS: {saveResult[4..].Trim()}");
                }
                else
                {
                    ed.WriteMessage($"\nTCM-ROADS: {saveResult}");
                }
            }
#endif
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static void AddTerrainPointsFromBlockCore(Editor ed, Database db)
    {
        var peo = new PromptEntityOptions("\nIzaberite jedan blok kao sablon (atribut = visina): ")
        {
            AllowNone = false
        };
        peo.SetRejectMessage("\nPotreban je blok (INSERT).");
        peo.AddAllowedClass(typeof(BlockReference), exactMatch: false);

        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
        {
            return;
        }

        string blockName;
        List<TerrainBlockAttributeRow> attrs;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(per.ObjectId, OpenMode.ForRead) is not BlockReference sample || sample.IsErased)
            {
                ed.WriteMessage("\nTCM-ROADS: Nije izabran validan blok.");
                tr.Commit();
                return;
            }

            blockName = GetEffectiveBlockName(tr, sample);
            attrs = ReadBlockAttributes(tr, sample);
            tr.Commit();
        }

        if (attrs.Count == 0)
        {
            ed.WriteMessage(
                $"\nTCM-ROADS: Blok „{blockName}“ nema atribute. Potreban je atribut sa visinom.");
            return;
        }

        TerrainBlockPointMapping? mapping;
#if !BRICSCAD
        var dialog = new TerrainBlockPointDialog(blockName, attrs);
        if (AcApp.ShowModalWindow(dialog) != true || dialog.Result is null)
        {
            return;
        }

        mapping = dialog.Result;
#else
        mapping = new TerrainBlockPointMapping
        {
            BlockName = blockName,
            ElevationAttributeTag = attrs[0].Tag,
            XySource = TerrainBlockXySource.BlockInsertion
        };
#endif
        TerrainPointBlockPreferences.Save(mapping);

        ed.WriteMessage(
            $"\nTCM-ROADS: Obeležite instance bloka „{mapping.BlockName}“ " +
            $"(Z = atribut {mapping.ElevationAttributeTag}).");

        var filter = new SelectionFilter(
        [
            new TypedValue((int)DxfCode.Start, "INSERT"),
            new TypedValue((int)DxfCode.BlockName, mapping.BlockName)
        ]);

        var selOpts = new PromptSelectionOptions
        {
            MessageForAdding = $"\nIzaberite blokove „{mapping.BlockName}“: ",
            RejectObjectsOnLockedLayers = false
        };

        var sel = ed.GetSelection(selOpts, filter);
        if (sel.Status != PromptStatus.OK || sel.Value is null || sel.Value.Count == 0)
        {
            // Fallback: bez filtera po imenu (dynamic / anonymous) pa filtrirati u kodu.
            sel = ed.GetSelection(selOpts);
            if (sel.Status != PromptStatus.OK || sel.Value is null || sel.Value.Count == 0)
            {
                ed.WriteMessage("\nTCM-ROADS: Nista nije izabrano.");
                return;
            }
        }

        var collected = new List<TerrainPointVm>();
        var skipped = 0;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            foreach (SelectedObject so in sel.Value)
            {
                if (so is null)
                {
                    continue;
                }

                if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is not BlockReference br || br.IsErased)
                {
                    continue;
                }

                if (!string.Equals(GetEffectiveBlockName(tr, br), mapping.BlockName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryExtractPointFromBlock(tr, br, mapping, out var pt))
                {
                    skipped++;
                    continue;
                }

                collected.Add(new TerrainPointVm(pt.X, pt.Y, pt.Z, isAdded: true));
            }

            if (collected.Count == 0)
            {
                ed.WriteMessage(
                    "\nTCM-ROADS: Nijedna tacka nije izvucena iz blokova" +
                    (skipped > 0 ? $" (preskoceno {skipped} bez validnog Z)." : "."));
                tr.Commit();
                return;
            }

            var working = LoadStoredAsVm(tr, db);
            MergeCollected(working, collected);
            PersistAndSyncPoints(tr, db, working, eraseHandles: null);
            var hadFaces = CountTerrainFaces(tr, db) > 0;
            tr.Commit();

            ed.WriteMessage(
                $"\nTCM-ROADS: Dodato {collected.Count} tacaka iz bloka „{mapping.BlockName}“" +
                (skipped > 0 ? $" (preskočeno {skipped})." : ".") +
                $" Ukupno u skupu: {working.Count}.");

            if (hadFaces)
            {
                RebuildTerrainFaces(ed, db, announce: true);
            }

            ShowTerrainPointsSummary(working, appendMode: true, rebuiltTin: hadFaces);
        }
    }

    private static string GetEffectiveBlockName(Transaction tr, BlockReference br)
    {
        try
        {
            if (br.IsDynamicBlock)
            {
                var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                if (!string.IsNullOrWhiteSpace(dyn.Name) && !dyn.Name.StartsWith("*", StringComparison.Ordinal))
                {
                    return dyn.Name;
                }
            }
        }
        catch
        {
            // fall through
        }

        return br.Name;
    }

    private static List<TerrainBlockAttributeRow> ReadBlockAttributes(Transaction tr, BlockReference br)
    {
        var list = new List<TerrainBlockAttributeRow>();
        if (br.AttributeCollection is null)
        {
            return list;
        }

        foreach (ObjectId id in br.AttributeCollection)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeReference ar || ar.IsErased)
            {
                continue;
            }

            list.Add(new TerrainBlockAttributeRow
            {
                Tag = ar.Tag ?? string.Empty,
                Value = ar.TextString ?? string.Empty
            });
        }

        return list;
    }

    private static bool TryExtractPointFromBlock(
        Transaction tr,
        BlockReference br,
        TerrainBlockPointMapping mapping,
        out Point3d point)
    {
        point = default;
        AttributeReference? elevAttr = null;
        if (br.AttributeCollection is not null)
        {
            foreach (ObjectId id in br.AttributeCollection)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not AttributeReference ar || ar.IsErased)
                {
                    continue;
                }

                if (string.Equals(ar.Tag, mapping.ElevationAttributeTag, StringComparison.OrdinalIgnoreCase))
                {
                    elevAttr = ar;
                    break;
                }
            }
        }

        if (elevAttr is null || !TryParseElevation(elevAttr.TextString, out var z))
        {
            return false;
        }

        var xy = mapping.XySource == TerrainBlockXySource.ElevationAttributePosition
            ? elevAttr.Position
            : br.Position;

        point = new Point3d(xy.X, xy.Y, z);
        return true;
    }

    private static bool TryParseElevation(string? text, out double z)
    {
        z = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().Replace(',', '.');
        // dozvoli "123.45m", "Z=12.3" itd.
        var start = -1;
        var end = -1;
        for (var i = 0; i < cleaned.Length; i++)
        {
            var c = cleaned[i];
            var digit = char.IsDigit(c) || c is '.' or '+' or '-';
            if (digit && start < 0)
            {
                start = i;
                end = i;
            }
            else if (digit)
            {
                end = i;
            }
            else if (start >= 0)
            {
                break;
            }
        }

        if (start < 0)
        {
            return false;
        }

        return double.TryParse(cleaned.Substring(start, end - start + 1),
            NumberStyles.Float, CultureInfo.InvariantCulture, out z);
    }

    private void RunTerrainPointsWorkflow(TerrainPointsMode mode)
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
            switch (mode)
            {
                case TerrainPointsMode.EditExisting:
                    OpenExistingTerrainPointsEditor(ed, db);
                    return;
                case TerrainPointsMode.LoadFile:
                    LoadTerrainPointsFromFileUi(ed, db);
                    return;
                case TerrainPointsMode.SaveFile:
                    SaveTerrainPointsFromStore(ed, db);
                    return;
            }

            var append = mode == TerrainPointsMode.SelectAppend;
            if (mode == TerrainPointsMode.InteractiveAsk)
            {
                using var peekTr = db.TransactionManager.StartTransaction();
                if (TerrainPointStore.HasPoints(peekTr, db))
                {
                    var kw = new PromptKeywordOptions(
                        "\nPostoje sacuvane tacke terena [Dodaj/Zameni] <Dodaj>: ")
                    {
                        AllowNone = true
                    };
                    kw.Keywords.Add("Dodaj");
                    kw.Keywords.Add("Zameni");
                    kw.Keywords.Default = "Dodaj";
                    var kwResult = ed.GetKeywords(kw);
                    if (kwResult.Status == PromptStatus.Cancel)
                    {
                        peekTr.Commit();
                        return;
                    }

                    append = kwResult.Status != PromptStatus.OK ||
                             !string.Equals(kwResult.StringResult, "Zameni", StringComparison.OrdinalIgnoreCase);
                }

                peekTr.Commit();
            }

            var collected = CollectTerrainPointsWithIds(ed, db, requireThreeNew: !append);
            if (collected.Count == 0)
            {
                ed.WriteMessage("\nTCM-ROADS: Nije izabrana nijedna tacka.");
                return;
            }

            if (append)
            {
                foreach (var p in collected)
                {
                    p.IsAdded = true;
                }
            }

            List<TerrainPointVm> working;
            bool hadFaces;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (append)
                {
                    working = LoadStoredAsVm(tr, db);
                    MergeCollected(working, collected);
                }
                else
                {
                    working = DeduplicateVm(collected);
                    // Novi skup tacaka — stare swap/delete operacije vise nisu validne.
                    TerrainDefinitionStore.ClearEditOps(tr, db);
                }

                if (working.Count < 3)
                {
                    ed.WriteMessage(
                        $"\nTCM-ROADS: Potrebne su najmanje 3 tacke (ukupno {working.Count}).");
                    tr.Commit();
                    return;
                }

                PersistAndSyncPoints(tr, db, working, eraseHandles: null);
                hadFaces = CountTerrainFaces(tr, db) > 0;
                tr.Commit();
            }

            ed.WriteMessage(
                $"\nTCM-ROADS: Sacuvano {working.Count} tacaka za teren" +
                (append ? " (dodato u postojeci skup)." : "."));

            var rebuiltTin = false;
            if (hadFaces || append)
            {
                RebuildTerrainFaces(ed, db, announce: true);
                rebuiltTin = true;
            }
            else
            {
                ed.WriteMessage(
                    " Koristite „3DFACE teren“ u prozoru ili TCMTERFACE za TIN.");
            }

            ShowTerrainPointsSummary(working, append, rebuiltTin);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static void OpenExistingTerrainPointsEditor(Editor ed, Database db)
    {
        List<TerrainPointVm> working;
        var hadFaces = false;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            working = LoadStoredAsVm(tr, db);
            hadFaces = CountTerrainFaces(tr, db) > 0;
            tr.Commit();
        }

        if (working.Count == 0)
        {
            ed.WriteMessage(
                "\nTCM-ROADS: Nema sacuvanih tacaka. Koristite Izaberi tacke / Dodaj tacke / Ucitaj tacke.");
            return;
        }

        ShowTerrainPointsSummary(working, appendMode: true, rebuiltTin: hadFaces);
    }

    /// <summary>Otvara editor tačaka za imenovani skup (iz prozora Projekat).</summary>
    internal static void OpenNamedTerrainPointsEditor(string surfaceName, System.Windows.Window? owner = null)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

#if BRICSCAD
        doc.Editor.WriteMessage("\nTCM-ROADS: Uredjivanje tacaka zahteva WPF dijalog (AutoCAD).");
        return;
#else
        List<TerrainPointVm> working;
        var hadFaces = false;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (!NamedTerrainSurfaceStore.ActivateSurface(tr, doc.Database, surfaceName, out var pts) ||
                pts.Count == 0)
            {
                tr.Commit();
                System.Windows.MessageBox.Show(
                    owner,
                    $"Skup tacaka „{surfaceName}“ nije pronadjen ili je prazan.",
                    "TCM-ROADS",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            working = pts.Select(p =>
            {
                var handle = FindDbPointHandle(tr, doc.Database, p);
                return new TerrainPointVm(p.X, p.Y, p.Z, handle, isAdded: false);
            }).ToList();
            PersistAndSyncPoints(tr, doc.Database, working, eraseHandles: null);
            hadFaces = CountTerrainFaces(tr, doc.Database) > 0;
            tr.Commit();
        }

        ShowTerrainPointsSummary(
            working,
            appendMode: true,
            rebuiltTin: hadFaces,
            statusHint: $"Skup tacaka: {surfaceName}",
            namedSurfaceName: surfaceName);
#endif
    }

    private static void LoadTerrainPointsFromFileUi(Editor ed, Database db)
    {
#if !BRICSCAD
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Ucitaj tacke terena",
            Filter = TerrainPointFile.FileFilter,
            CheckFileExists = true
        };
        var folder = ProjectFolderPreferences.FolderPath;
        if (System.IO.Directory.Exists(folder))
        {
            dlg.InitialDirectory = folder;
        }

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        List<Point3d> loaded;
        try
        {
            loaded = TerrainPointFile.Read(dlg.FileName);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS: Ne mogu da ucitam fajl: {ex.Message}");
            return;
        }

        if (loaded.Count == 0)
        {
            ed.WriteMessage("\nTCM-ROADS: Fajl ne sadrzi validne X,Y,Z tacke.");
            return;
        }

        List<TerrainPointVm> working;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            working = LoadStoredAsVm(tr, db);
            if (working.Count > 0)
            {
                var kw = new PromptKeywordOptions(
                    $"\nUcitano {loaded.Count} tacaka. [Dodaj/Zameni] postojece ({working.Count}) <Dodaj>: ")
                {
                    AllowNone = true
                };
                kw.Keywords.Add("Dodaj");
                kw.Keywords.Add("Zameni");
                kw.Keywords.Default = "Dodaj";
                var kwResult = ed.GetKeywords(kw);
                if (kwResult.Status == PromptStatus.Cancel)
                {
                    tr.Commit();
                    return;
                }

                if (kwResult.Status == PromptStatus.OK &&
                    string.Equals(kwResult.StringResult, "Zameni", StringComparison.OrdinalIgnoreCase))
                {
                    var erase = working.Where(p => p.PointHandle != 0).Select(p => p.PointHandle).ToList();
                    working = loaded.Select(p => new TerrainPointVm(p.X, p.Y, p.Z, isAdded: true)).ToList();
                    PersistAndSyncPoints(tr, db, working, erase);
                }
                else
                {
                    MergeCollected(working, loaded.Select(p => new TerrainPointVm(p.X, p.Y, p.Z, isAdded: true)).ToList());
                    PersistAndSyncPoints(tr, db, working, eraseHandles: null);
                }
            }
            else
            {
                working = loaded.Select(p => new TerrainPointVm(p.X, p.Y, p.Z, isAdded: true)).ToList();
                PersistAndSyncPoints(tr, db, working, eraseHandles: null);
            }

            tr.Commit();
        }

        ed.WriteMessage($"\nTCM-ROADS: Ucitano {working.Count} tacaka. Zatim TCMTERFACE za TIN.");
        ShowTerrainPointsSummary(working, appendMode: true, rebuiltTin: false);
#else
        ed.WriteMessage("\nTCM-ROADS: Ucitavanje fajla nije dostupno u ovoj konfiguraciji.");
#endif
    }

    private static void SaveTerrainPointsFromStore(Editor ed, Database db)
    {
        List<TerrainPointVm> rows;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            rows = LoadStoredAsVm(tr, db);
            tr.Commit();
        }

        if (rows.Count == 0)
        {
            ed.WriteMessage("\nTCM-ROADS: Nema tacaka za snimanje.");
            return;
        }

        try
        {
            var path = ExportTerrainPointsToProject(rows);
            ed.WriteMessage(path is null
                ? "\nTCM-ROADS: Nema tacaka za snimanje."
                : $"\nTCM-ROADS: Tacke snimljene:\n{path}");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS: Greska pri snimanju: {ex.Message}");
        }
    }

    private static void ShowTerrainPointsSummary(
        IReadOnlyList<TerrainPointVm> points,
        bool appendMode,
        bool rebuiltTin,
        string? statusHint = null,
        string? namedSurfaceName = null)
    {
#if !BRICSCAD
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        try
        {
            var dialog = new TerrainPointsSummaryDialog(
                points,
                appendMode,
                rebuiltTin,
                applyToDrawing: (rows, eraseHandles) =>
                    RunWithDocumentAccess(doc, () =>
                    {
                        using var tr = doc.Database.TransactionManager.StartTransaction();
                        PersistAndSyncPoints(tr, doc.Database, rows, eraseHandles);
                        if (!string.IsNullOrWhiteSpace(namedSurfaceName))
                        {
                            NamedTerrainSurfaceStore.SaveSurface(
                                tr, doc.Database, namedSurfaceName,
                                rows.Select(r => r.ToPoint3d()).ToList(),
                                setActive: true);
                        }

                        tr.Commit();
                    }),
                buildTerrain: (rows, eraseHandles, terrainName) =>
                    RunWithDocumentAccess(doc, () =>
                    {
                        using (var tr = doc.Database.TransactionManager.StartTransaction())
                        {
                            PersistAndSyncPoints(tr, doc.Database, rows, eraseHandles);
                            tr.Commit();
                        }

                        RebuildTerrainFaces(doc.Editor, doc.Database, announce: true, terrainName);
                    }),
                savePointsToProject: (pointSetName, rows) =>
                {
                    try
                    {
                        var name = string.IsNullOrWhiteSpace(pointSetName)
                            ? namedSurfaceName
                            : pointSetName;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            using var tr = doc.Database.TransactionManager.StartTransaction();
                            name = NamedTerrainSurfaceStore.GetActiveName(tr, doc.Database)
                                   ?? NamedTerrainSurfaceStore.SuggestNextName(tr, doc.Database);
                            tr.Commit();
                        }

                        return SavePointSetToProject(
                            doc.Database, name!, rows, setActive: true);
                    }
                    catch (System.Exception ex)
                    {
                        return "ERR:" + ex.Message;
                    }
                },
                pickPoint: () =>
                {
                    var prompt = new PromptPointOptions("\nNova tacka terena: ")
                    {
                        AllowNone = true
                    };
                    var result = doc.Editor.GetPoint(prompt);
                    return result.Status == PromptStatus.OK ? result.Value : null;
                },
                pickDrawingPoint: () =>
                {
                    var peo = new PromptEntityOptions(
                        "\nIzaberite tacku terena (POINT) u crtezu: ")
                    {
                        AllowNone = true
                    };
                    peo.SetRejectMessage("\nPotrebna je POINT tacka.");
                    peo.AddAllowedClass(typeof(DBPoint), exactMatch: true);
                    var per = doc.Editor.GetEntity(peo);
                    if (per.Status != PromptStatus.OK)
                    {
                        return null;
                    }

                    return per.ObjectId.Handle.Value;
                },
                listNamedTerrains: () =>
                {
                    using var tr = doc.Database.TransactionManager.StartTransaction();
                    var names = NamedTerrainSurfaceStore.ListNames(tr, doc.Database);
                    var active = !string.IsNullOrWhiteSpace(namedSurfaceName)
                        ? namedSurfaceName
                        : NamedTerrainSurfaceStore.GetActiveName(tr, doc.Database);
                    var suggested = NamedTerrainSurfaceStore.SuggestNextName(tr, doc.Database);
                    tr.Commit();
                    return (names, active, suggested);
                },
                loadNamedTerrain: name =>
                {
                    try
                    {
                        using var tr = doc.Database.TransactionManager.StartTransaction();
                        if (!NamedTerrainSurfaceStore.ActivateSurface(
                                tr, doc.Database, name, out var pts))
                        {
                            tr.Commit();
                            return (null, $"Teren „{name}“ nije pronadjen.");
                        }

                        var vms = pts.Select(p =>
                        {
                            var handle = FindDbPointHandle(tr, doc.Database, p);
                            return new TerrainPointVm(p.X, p.Y, p.Z, handle, isAdded: true);
                        }).ToList();
                        PersistAndSyncPoints(tr, doc.Database, vms, eraseHandles: null);
                        tr.Commit();
                        return (vms, null);
                    }
                    catch (System.Exception ex)
                    {
                        return (null, ex.Message);
                    }
                },
                statusHint: statusHint);

            AcApp.ShowModalWindow(dialog);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage(
                $"\nTCM-ROADS: Pregled tacaka nije otvoren: {ex.Message}");
        }
#endif
    }

    /// <summary>
    /// Poziva se iz modalne komande / WPF dijaloga — NE radi LockDocument
    /// (vec je zakljucan; nested lock = eLockViolation ili FATAL pri zatvaranju).
    /// </summary>
    private static string? RunWithDocumentAccess(Autodesk.AutoCAD.ApplicationServices.Document doc, Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (System.Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Snima samo XYZ tacke (CSV) u folder projekta — kasnije ucitavanje u bilo koji crtez + TCMTERFACE.
    /// </summary>
    private static string? ExportTerrainPointsToProject(IReadOnlyList<TerrainPointVm> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var folder = ProjectFolderPreferences.EnsureFolder();
        var fileName = $"TCM_TEREN_TACKE_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var path = System.IO.Path.Combine(folder, fileName);
        return TerrainPointFile.Write(path, rows.Select(r => r.ToPoint3d()).ToList());
    }

    private static List<TerrainPointVm> LoadStoredAsVm(Transaction tr, Database db)
    {
        var list = new List<TerrainPointVm>();
        foreach (var p in TerrainPointStore.Load(tr, db))
        {
            var handle = FindDbPointHandle(tr, db, p);
            list.Add(new TerrainPointVm(p.X, p.Y, p.Z, handle));
        }

        return DeduplicateVm(list);
    }

    private static void MergeCollected(List<TerrainPointVm> working, IReadOnlyList<TerrainPointVm> collected)
    {
        foreach (var p in collected)
        {
            var dup = working.FirstOrDefault(q =>
                Math.Abs(q.X - p.X) <= 1e-8 && Math.Abs(q.Y - p.Y) <= 1e-8);
            if (dup is null)
            {
                working.Add(p);
            }
            else
            {
                dup.Z = p.Z;
                if (dup.PointHandle == 0 && p.PointHandle != 0)
                {
                    dup.PointHandle = p.PointHandle;
                }
            }
        }
    }

    private static List<TerrainPointVm> DeduplicateVm(IReadOnlyList<TerrainPointVm> points)
    {
        var unique = new List<TerrainPointVm>(points.Count);
        foreach (var p in points)
        {
            var dup = unique.FirstOrDefault(q =>
                Math.Abs(q.X - p.X) <= 1e-8 && Math.Abs(q.Y - p.Y) <= 1e-8);
            if (dup is null)
            {
                unique.Add(new TerrainPointVm(p.X, p.Y, p.Z, p.PointHandle, p.IsAdded));
            }
            else
            {
                dup.Z = p.Z;
                if (dup.PointHandle == 0 && p.PointHandle != 0)
                {
                    dup.PointHandle = p.PointHandle;
                }
            }
        }

        return unique;
    }

    /// <summary>
    /// Snima skup u NOD i sinhronizuje DBPOINT entitete u Model Space.
    /// Handle-ovi novih DBPoint-ova vracaju se i u originalne VM redove.
    /// </summary>
    private static void PersistAndSyncPoints(
        Transaction tr,
        Database db,
        IReadOnlyList<TerrainPointVm> rows,
        IReadOnlyCollection<long>? eraseHandles)
    {
        var clean = DeduplicateVm(rows);
        TerrainPointStore.Save(tr, db, clean.Select(r => r.ToPoint3d()).ToList());

        EnsureTerrainLayer(tr, db);
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        foreach (var row in clean)
        {
            if (row.PointHandle != 0 &&
                TryGetDbPoint(tr, db, row.PointHandle, out var existing) &&
                existing is not null)
            {
                existing.UpgradeOpen();
                existing.Position = row.ToPoint3d();
                existing.Layer = TerrainLayerName;
                continue;
            }

            var pt = new DBPoint(row.ToPoint3d()) { Layer = TerrainLayerName };
            modelSpace.AppendEntity(pt);
            tr.AddNewlyCreatedDBObject(pt, true);
            row.PointHandle = pt.Handle.Value;
        }

        // Propagiraj handle nazad u originalne VM redove (DeduplicateVm pravi klonove).
        const double tol = 1e-8;
        foreach (var original in rows)
        {
            var match = clean.FirstOrDefault(c =>
                Math.Abs(c.X - original.X) <= tol && Math.Abs(c.Y - original.Y) <= tol);
            if (match is not null && match.PointHandle != 0)
            {
                original.PointHandle = match.PointHandle;
                original.Z = match.Z;
            }
        }

        if (eraseHandles is null || eraseHandles.Count == 0)
        {
            return;
        }

        var kept = new HashSet<long>(clean.Select(r => r.PointHandle).Where(h => h != 0));
        foreach (var handleValue in eraseHandles)
        {
            if (handleValue == 0 || kept.Contains(handleValue))
            {
                continue;
            }

            if (!TryGetDbPoint(tr, db, handleValue, out var doomed) || doomed is null)
            {
                continue;
            }

            doomed.UpgradeOpen();
            doomed.Erase();
        }
    }

    /// <summary>
    /// Snima imenovani skup, sinhronizuje DB tacke i registruje ime u aktivnom projektu.
    /// </summary>
    private static string? SavePointSetToProject(
        Database db,
        string pointSetName,
        IReadOnlyList<TerrainPointVm> rows,
        bool setActive)
    {
        if (rows.Count == 0)
        {
            return "Nema tacaka za snimanje.";
        }

        string name;
        try
        {
            name = NamedTerrainSurfaceStore.NormalizeName(pointSetName);
        }
        catch (System.Exception ex)
        {
            return "ERR:" + ex.Message;
        }

        using (var tr = db.TransactionManager.StartTransaction())
        {
            PersistAndSyncPoints(tr, db, rows, eraseHandles: null);
            NamedTerrainSurfaceStore.SaveSurface(
                tr, db, name, rows.Select(r => r.ToPoint3d()).ToList(), setActive);
            tr.Commit();
        }

        var projectError = TcmProjectStore.AddPointSetToActiveProject(name);
        return projectError is null
            ? $"Skup tacaka „{name}“ snimljen u crtez i aktivni TCM projekat."
            : "ERR:" + projectError;
    }

    private static long FindDbPointHandle(Transaction tr, Database db, Point3d p)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);
        const double tol = 1e-6;
        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not DBPoint dbPoint || dbPoint.IsErased)
            {
                continue;
            }

            if (!string.Equals(dbPoint.Layer, TerrainLayerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var q = dbPoint.Position;
            if (Math.Abs(q.X - p.X) <= tol &&
                Math.Abs(q.Y - p.Y) <= tol &&
                Math.Abs(q.Z - p.Z) <= tol)
            {
                return dbPoint.Handle.Value;
            }
        }

        return 0;
    }

    private static bool TryGetDbPoint(Transaction tr, Database db, long handleValue, out DBPoint? point)
    {
        point = null;
        try
        {
            var handle = new Handle(handleValue);
            if (!db.TryGetObjectId(handle, out var id) || id.IsNull)
            {
                return false;
            }

            point = tr.GetObject(id, OpenMode.ForRead) as DBPoint;
            return point is not null && !point.IsErased;
        }
        catch
        {
            return false;
        }
    }

    [CommandMethod("TCMTERFACE", CommandFlags.Modal)]
    public void BuildTerrainFaces()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        try
        {
            List<TerrainPointVm> working;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                working = TerrainPointStore.Load(tr, doc.Database)
                    .Select(p => new TerrainPointVm(p.X, p.Y, p.Z))
                    .ToList();
                tr.Commit();
            }

            if (working.Count < 3)
            {
                ShowTerrainPointsSummary(
                    working,
                    appendMode: true,
                    rebuiltTin: false,
                    statusHint:
                    "Nema dovoljno tacaka za 3DFACE teren (potrebne su najmanje 3). " +
                    "Dodajte / ucitajte tacke ili „Ucitaj teren“, zatim kliknite „3DFACE teren“ i unesite ime.");
                return;
            }

            IReadOnlyList<string> names;
            string suggested;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                names = NamedTerrainSurfaceStore.ListNames(tr, doc.Database);
                suggested = NamedTerrainSurfaceStore.SuggestNextName(tr, doc.Database);
                tr.Commit();
            }

            var nameDlg = new NamedTerrainDialog(names, suggested);
            if (AcApp.ShowModalWindow(nameDlg) != true)
            {
                // Korisnik odustao — otvori editor tacaka radi kontrole skupa.
                ShowTerrainPointsSummary(working, appendMode: true, rebuiltTin: false);
                return;
            }

            RebuildTerrainFaces(doc.Editor, doc.Database, announce: true, nameDlg.TerrainName);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>
    /// Civil 3D Add Line: povezuje dva postojeća TIN temena (ili duž polilinije) i
    /// ponovo trianguliše tako da nova ivica postoji u mreži.
    /// </summary>
    [CommandMethod("TCMTERADDLINE", CommandFlags.Modal)]
    public void AddTerrainTinLine()
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
            string? activeSurface;
            EditableTerrainMesh editable;
            using (var initTr = db.TransactionManager.StartTransaction())
            {
                activeSurface = NamedTerrainSurfaceStore.GetActiveName(initTr, db);
                editable = EditableTerrainMesh.Build(
                    LoadTerrainFaces(initTr, db, activeSurface),
                    SwapEdgePickTolerance);
                initTr.Commit();
            }

            if (editable.Triangles.Count == 0)
            {
                ed.WriteMessage(
                    "\nTCM-ROADS: Nema 3DFACE aktivnog terena. Prvo TCMTERFACE.");
                return;
            }

            var added = 0;
            while (true)
            {
                var prompt = new PromptPointOptions(
                    "\nPrvo teme TIN linije [Linija] (Enter = kraj): ")
                {
                    AllowNone = true
                };
                prompt.Keywords.Add("Linija");
                var first = ed.GetPoint(prompt);

                if (first.Status == PromptStatus.Keyword &&
                    string.Equals(first.StringResult, "Linija", StringComparison.OrdinalIgnoreCase))
                {
                    added += AddTerrainLinesFromCurve(ed, db);
                    using var reloadTr = db.TransactionManager.StartTransaction();
                    activeSurface = NamedTerrainSurfaceStore.GetActiveName(reloadTr, db);
                    editable = EditableTerrainMesh.Build(
                        LoadTerrainFaces(reloadTr, db, activeSurface),
                        SwapEdgePickTolerance);
                    reloadTr.Commit();
                    continue;
                }

                if (first.Status != PromptStatus.OK)
                {
                    break;
                }

                var secondOpts = new PromptPointOptions("\nDrugo teme TIN linije: ")
                {
                    AllowNone = false,
                    UseBasePoint = true,
                    BasePoint = first.Value
                };
                var second = ed.GetPoint(secondOpts);
                if (second.Status != PromptStatus.OK)
                {
                    break;
                }

                if (!editable.TrySnap(first.Value, out var a) ||
                    !editable.TrySnap(second.Value, out var b))
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Kliknite blizu postojecih TIN temena (kao Civil Add Line).");
                    continue;
                }

                if (XyDistance(a, b) < 1e-6)
                {
                    ed.WriteMessage("\nTCM-ROADS: Temena moraju biti razlicita.");
                    continue;
                }

                if (!TryApplyAddLineLocally(db, editable, activeSurface, a, b, out var failure))
                {
                    ShowAddLineBlockedMessage(failure);
                    continue;
                }

                added++;
                ed.WriteMessage(
                    "\nTCM-ROADS: Add Line — lokalni edge-flip zavrsen (sacuvano za TCMTERFACE).");
            }

            if (added > 0)
            {
                if (RefreshContoursIfDrawn(writeMessage: false))
                {
                    ed.WriteMessage("\nTCM-ROADS: Izohipse osvezene jednom po zavrsetku Add Line.");
                }

                ed.Regen();
                ed.WriteMessage($"\nTCM-ROADS: Ukupno Add Line: {added}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>
    /// Civil Add Continuous Line: lanac povezanih TIN ivica — prvo teme, zatim
    /// sledeća bez prekida (Enter = kraj lanca).
    /// </summary>
    [CommandMethod("TCMTERADDCLINE", CommandFlags.Modal)]
    public void AddTerrainTinContinuousLine()
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
            var added = 0;
            while (true)
            {
                var startOpts = new PromptPointOptions(
                    "\nPrvo teme neprekidne TIN linije [Linija] (Enter = kraj): ")
                {
                    AllowNone = true
                };
                startOpts.Keywords.Add("Linija");
                var start = ed.GetPoint(startOpts);

                if (start.Status == PromptStatus.Keyword &&
                    string.Equals(start.StringResult, "Linija", StringComparison.OrdinalIgnoreCase))
                {
                    added += AddTerrainLinesFromCurve(ed, db);
                    continue;
                }

                if (start.Status != PromptStatus.OK)
                {
                    break;
                }

                Point3d current;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var faces = LoadTerrainFaces(tr, db);
                    if (faces.Count == 0)
                    {
                        ed.WriteMessage(
                            "\nTCM-ROADS: Nema 3DFACE terena. Prvo TCMTERFACE.");
                        tr.Commit();
                        return;
                    }

                    if (!TrySnapToTinVertex(start.Value, faces, SwapEdgePickTolerance, out current))
                    {
                        ed.WriteMessage(
                            "\nTCM-ROADS: Kliknite blizu postojeceg TIN temena.");
                        tr.Commit();
                        continue;
                    }

                    tr.Commit();
                }

                var chainSegments = 0;
                while (true)
                {
                    var nextOpts = new PromptPointOptions(
                        "\nSledece teme neprekidne linije (Enter = kraj lanca): ")
                    {
                        AllowNone = true,
                        UseBasePoint = true,
                        BasePoint = current
                    };
                    var nextPick = ed.GetPoint(nextOpts);
                    if (nextPick.Status != PromptStatus.OK)
                    {
                        break;
                    }

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var faces = LoadTerrainFaces(tr, db);
                        if (!TrySnapToTinVertex(nextPick.Value, faces, SwapEdgePickTolerance, out var next))
                        {
                            ed.WriteMessage(
                                "\nTCM-ROADS: Kliknite blizu postojeceg TIN temena.");
                            tr.Commit();
                            continue;
                        }

                        if (XyDistance(current, next) < 1e-6)
                        {
                            ed.WriteMessage("\nTCM-ROADS: Temena moraju biti razlicita.");
                            tr.Commit();
                            continue;
                        }

                        TerrainDefinitionStore.AddForcedEdge(tr, db, current, next);
                        tr.Commit();
                        current = next;
                    }

                    RebuildTerrainFaces(ed, db, announce: false);
                    chainSegments++;
                    added++;
                    ed.WriteMessage(
                        "\nTCM-ROADS: Add Continuous Line — segment forsiran, TIN osvezen.");
                }

                if (chainSegments > 0)
                {
                    ed.WriteMessage(
                        $"\nTCM-ROADS: Lanac zavrsen ({chainSegments} segment(a)).");
                }
            }

            if (added > 0)
            {
                ed.WriteMessage($"\nTCM-ROADS: Ukupno Add Continuous Line: {added}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>
    /// Civil Add Line duž polilinije/linije: svaki segment između temena → forsira ivicu.
    /// Temena van TIN-a se dodaju u skup tačaka.
    /// </summary>
    private static int AddTerrainLinesFromCurve(Editor ed, Database db)
    {
        var peo = new PromptEntityOptions(
            "\nIzaberite liniju / polylinu duz koje se prave TIN ivice: ")
        {
            AllowNone = false
        };
        peo.SetRejectMessage("\nSamo Line ili LWPOLYLINE.");
        peo.AddAllowedClass(typeof(Line), exactMatch: false);
        peo.AddAllowedClass(typeof(Polyline), exactMatch: false);
        peo.AddAllowedClass(typeof(Polyline3d), exactMatch: false);

        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
        {
            return 0;
        }

        List<Point3d> pathPts;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(per.ObjectId, OpenMode.ForRead) is not Curve curve)
            {
                tr.Commit();
                return 0;
            }

            pathPts = SampleCurveVertices(tr, curve);
            tr.Commit();
        }

        if (pathPts.Count < 2)
        {
            ed.WriteMessage("\nTCM-ROADS: Linija nema dovoljno temena.");
            return 0;
        }

        var segments = 0;
        List<(Point3d A, Point3d B)> candidateEdges;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            RoadDrawing.EnsureRegApp(tr, db);
            var points = TerrainPointStore.Load(tr, db).ToList();
            var faces = LoadTerrainFaces(tr, db);
            var snapped = new List<Point3d>(pathPts.Count);

            foreach (var raw in pathPts)
            {
                if (faces.Count > 0 &&
                    TrySnapToTinVertex(raw, faces, SwapEdgePickTolerance * 2, out var tinPt))
                {
                    snapped.Add(tinPt);
                    continue;
                }

                // Novo teme — ubaci u skup tacaka (kao Civil kada linija dodaje vertex).
                MergePoint(points, raw);
                snapped.Add(raw);
            }

            candidateEdges = new List<(Point3d A, Point3d B)>();
            for (var i = 0; i + 1 < snapped.Count; i++)
            {
                if (XyDistance(snapped[i], snapped[i + 1]) < 1e-6)
                {
                    continue;
                }

                candidateEdges.Add((snapped[i], snapped[i + 1]));
            }

            if (candidateEdges.Count == 0)
            {
                tr.Commit();
                ed.WriteMessage("\nTCM-ROADS: Nema segmenata za Add Line.");
                return 0;
            }

            if (!TerrainTinConstraints.TryValidateForcedEdges(tr, db, points, candidateEdges, out var failure))
            {
                tr.Commit();
                ShowAddLineBlockedMessage(failure);
                return 0;
            }

            TerrainPointStore.Save(tr, db, points);
            TerrainDefinitionStore.AddForcedEdges(tr, db, candidateEdges);
            segments = candidateEdges.Count;

            tr.Commit();
        }

        if (segments == 0)
        {
            ed.WriteMessage("\nTCM-ROADS: Nema segmenata za Add Line.");
            return 0;
        }

        RebuildTerrainFaces(ed, db, announce: false);
        ed.WriteMessage(
            $"\nTCM-ROADS: Add Line duz linije — {segments} forsiran(ih) ivica, TIN osvezen.");
        return segments;
    }

    /// <summary>
    /// Primenjuje Add Line direktno na postojeće trouglove. Menja samo trouglove
    /// koje je edge-flip stvarno zahvatio; nema Delaunay-a ni punog redraw-a.
    /// </summary>
    private static bool TryApplyAddLineLocally(
        Database db,
        EditableTerrainMesh mesh,
        string? surfaceName,
        Point3d a,
        Point3d b,
        out string? failure)
    {
        if (!TerrainTinConstraints.TryEnforceEdgeOnExistingMesh(
                mesh.Vertices, mesh.Triangles, a, b, out var updated, out failure))
        {
            return false;
        }

        var oldKeys = mesh.FaceIdsByTriangle;
        var newByKey = updated
            .Select(t => (Key: EditableTerrainMesh.TriangleKey(t), Triangle: t))
            .ToDictionary(x => x.Key, x => x.Triangle);
        var removed = oldKeys.Keys.Where(k => !newByKey.ContainsKey(k)).ToList();
        var added = newByKey.Where(kv => !oldKeys.ContainsKey(kv.Key)).ToList();

        using var tr = db.TransactionManager.StartTransaction();
        RoadDrawing.EnsureRegApp(tr, db);
        foreach (var key in removed)
        {
            var id = oldKeys[key];
            if (!id.IsNull && !id.IsErased &&
                tr.GetObject(id, OpenMode.ForWrite) is Entity entity &&
                !entity.IsErased)
            {
                entity.Erase();
            }
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);
        var addedIds = new Dictionary<(int A, int B, int C), ObjectId>();
        foreach (var pair in added)
        {
            var key = pair.Key;
            var tri = pair.Value;
            var id = AppendTerrainFace(
                tr,
                modelSpace,
                mesh.Vertices[tri.A],
                mesh.Vertices[tri.B],
                mesh.Vertices[tri.C],
                surfaceName);
            addedIds[key] = id;
        }

        TerrainDefinitionStore.AddForcedEdge(tr, db, a, b);
        tr.Commit();

        foreach (var key in removed)
        {
            oldKeys.Remove(key);
        }

        foreach (var pair in addedIds)
        {
            oldKeys[pair.Key] = pair.Value;
        }

        mesh.Triangles.Clear();
        mesh.Triangles.AddRange(updated);
        return true;
    }

    /// <summary>Snapshot aktivnog TIN-a + prostorni indeks temena za Add Line.</summary>
    private sealed class EditableTerrainMesh
    {
        private readonly Dictionary<(int X, int Y), List<int>> _vertexGrid = new();
        private readonly double _snapTolerance;
        private readonly double _originX;
        private readonly double _originY;

        public List<Point3d> Vertices { get; } = [];
        public List<TerrainDelaunay.Triangle> Triangles { get; } = [];
        public Dictionary<(int A, int B, int C), ObjectId> FaceIdsByTriangle { get; } = new();

        private EditableTerrainMesh(double snapTolerance, double originX, double originY)
        {
            _snapTolerance = Math.Max(1e-6, snapTolerance);
            _originX = originX;
            _originY = originY;
        }

        public static EditableTerrainMesh Build(IReadOnlyList<Face> faces, double snapTolerance)
        {
            var all = new List<Point3d>(faces.Count * 3);
            foreach (var face in faces)
            {
                all.Add(face.GetVertexAt(0));
                all.Add(face.GetVertexAt(1));
                all.Add(face.GetVertexAt(2));
            }

            var originX = all.Count == 0 ? 0 : all.Min(p => p.X);
            var originY = all.Count == 0 ? 0 : all.Min(p => p.Y);
            var mesh = new EditableTerrainMesh(snapTolerance, originX, originY);
            var index = new Dictionary<(long X, long Y), int>();

            int VertexIndex(Point3d p)
            {
                var key = (
                    (long)Math.Round(p.X * 1_000_000.0),
                    (long)Math.Round(p.Y * 1_000_000.0));
                if (index.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var next = mesh.Vertices.Count;
                mesh.Vertices.Add(p);
                index[key] = next;
                var cell = mesh.Cell(p.X, p.Y);
                if (!mesh._vertexGrid.TryGetValue(cell, out var bucket))
                {
                    bucket = [];
                    mesh._vertexGrid[cell] = bucket;
                }

                bucket.Add(next);
                return next;
            }

            foreach (var face in faces)
            {
                var tri = new TerrainDelaunay.Triangle(
                    VertexIndex(face.GetVertexAt(0)),
                    VertexIndex(face.GetVertexAt(1)),
                    VertexIndex(face.GetVertexAt(2)));
                var key = TriangleKey(tri);
                if (mesh.FaceIdsByTriangle.ContainsKey(key))
                {
                    continue;
                }

                mesh.Triangles.Add(tri);
                mesh.FaceIdsByTriangle[key] = face.ObjectId;
            }

            return mesh;
        }

        public bool TrySnap(Point3d pick, out Point3d vertex)
        {
            vertex = default;
            var cell = Cell(pick.X, pick.Y);
            var best = _snapTolerance;
            var found = false;
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_vertexGrid.TryGetValue((cell.X + dx, cell.Y + dy), out var bucket))
                    {
                        continue;
                    }

                    foreach (var i in bucket)
                    {
                        var candidate = Vertices[i];
                        var distance = XyDistance(pick, candidate);
                        if (distance > best)
                        {
                            continue;
                        }

                        best = distance;
                        vertex = candidate;
                        found = true;
                    }
                }
            }

            return found;
        }

        public static (int A, int B, int C) TriangleKey(TerrainDelaunay.Triangle tri)
        {
            var values = new[] { tri.A, tri.B, tri.C };
            Array.Sort(values);
            return (values[0], values[1], values[2]);
        }

        private (int X, int Y) Cell(double x, double y) =>
            ((int)Math.Floor((x - _originX) / _snapTolerance),
             (int)Math.Floor((y - _originY) / _snapTolerance));
    }

    private static bool TryCommitAddLineSegment(Transaction tr, Database db, Point3d a, Point3d b)
    {
        var points = TerrainPointStore.Load(tr, db).ToList();
        if (!TerrainTinConstraints.TryValidateForcedEdge(tr, db, points, a, b, out var failure))
        {
            ShowAddLineBlockedMessage(failure);
            return false;
        }

        TerrainDefinitionStore.AddForcedEdge(tr, db, a, b);
        return true;
    }

    private static void ShowAddLineBlockedMessage(string? message)
    {
        MessageBox.Show(
            message ?? "Add Line nije moguce.",
            "TCM-ROADS — Add Line",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static List<Point3d> SampleCurveVertices(Transaction tr, Curve curve)
    {
        var pts = new List<Point3d>();
        switch (curve)
        {
            case Line line:
                pts.Add(line.StartPoint);
                pts.Add(line.EndPoint);
                break;
            case Polyline pl:
                for (var i = 0; i < pl.NumberOfVertices; i++)
                {
                    pts.Add(pl.GetPoint3dAt(i));
                }

                break;
            case Polyline3d pl3:
                foreach (ObjectId id in pl3)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is PolylineVertex3d v)
                    {
                        pts.Add(v.Position);
                    }
                }

                break;
            default:
                pts.Add(curve.StartPoint);
                pts.Add(curve.EndPoint);
                break;
        }

        return pts;
    }

    private static void MergePoint(List<Point3d> points, Point3d p)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (Math.Abs(points[i].X - p.X) <= 1e-6 && Math.Abs(points[i].Y - p.Y) <= 1e-6)
            {
                points[i] = p;
                return;
            }
        }

        points.Add(p);
    }

    private static bool TrySnapToTinVertex(
        Point3d pick,
        IReadOnlyList<Face> faces,
        double tolerance,
        out Point3d vertex)
    {
        vertex = default;
        var bestDist = tolerance;
        var found = false;
        foreach (var face in faces)
        {
            for (short i = 0; i < 4; i++)
            {
                var v = face.GetVertexAt(i);
                var d = XyDistance(pick, v);
                if (d <= bestDist)
                {
                    bestDist = d;
                    vertex = v;
                    found = true;
                }
            }
        }

        return found;
    }

    private static double XyDistance(Point3d a, Point3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Civil 3D Swap Edge: klik blizu zajednicke ivice dva trougla → zamena dijagonale
    /// ako je cetvorougao konveksan. Enter = kraj.
    /// </summary>
    [CommandMethod("TCMTERSWAP", CommandFlags.Modal)]
    public void SwapTerrainFaceEdge()
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
            var swapped = 0;
            while (true)
            {
                var prompt = new PromptPointOptions(
                    "\nIzaberite ivicu za zamenu (Enter = kraj): ")
                {
                    AllowNone = true
                };
                var pick = ed.GetPoint(prompt);
                if (pick.Status != PromptStatus.OK)
                {
                    break;
                }

                using var tr = db.TransactionManager.StartTransaction();
                RoadDrawing.EnsureRegApp(tr, db);

                var faces = LoadTerrainFaces(tr, db);
                if (faces.Count < 2)
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Nema dovoljno 3DFACE terena. Prvo TCMTERFACE.");
                    tr.Commit();
                    break;
                }

                if (!TryFindSharedEdgeNear(pick.Value, faces, SwapEdgePickTolerance, out var pair))
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Nema pogodne ivice (klik unutar 1 jedinice, " +
                        "dva trougla, konveksan cetvorougao).");
                    tr.Commit();
                    continue;
                }

                var (face1, face2, a, b, c, d) = pair;
                face1.UpgradeOpen();
                face2.UpgradeOpen();
                face1.Erase();
                face2.Erase();

                EnsureTerrainLayer(tr, db);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);

                // Civil swap: AB → CD. Novi trouglovi: A-C-D i B-C-D.
                AppendTerrainFace(tr, modelSpace, a, c, d);
                AppendTerrainFace(tr, modelSpace, b, c, d);
                TerrainDefinitionStore.AddForcedEdgeAfterSwap(tr, db, a, b, c, d);
                tr.Commit();
                swapped++;
                ed.WriteMessage("\nTCM-ROADS: Ivica zamenjena (sacuvano za TCMTERFACE).");
                if (RefreshContoursIfDrawn(writeMessage: false))
                {
                    ed.WriteMessage(" Izohipse osvezene.");
                }
            }

            if (swapped > 0)
            {
                ed.WriteMessage($"\nTCM-ROADS: Ukupno zamenjeno ivica: {swapped}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>
    /// Civil 3D Delete Line: uklanja TIN ivice (trouglovi koji ih sadrze) — void na rubu ili unutra.
    /// Klik unutar 1 jedinice od ivice, ili Prozor za crossing/window; Enter = kraj.
    /// </summary>
    [CommandMethod("TCMTERBRISI", CommandFlags.Modal)]
    public void DeleteTerrainFaceEdges()
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
            var deletedFaces = 0;
            TerrainFaceEdgeIndex? edgeIndex = null;
            while (true)
            {
                var prompt = new PromptPointOptions(
                    "\nIzaberite ivicu za brisanje [Prozor] (Enter = kraj): ")
                {
                    AllowNone = true
                };
                prompt.Keywords.Add("Prozor");
                var pick = ed.GetPoint(prompt);

                if (pick.Status == PromptStatus.Keyword &&
                    string.Equals(pick.StringResult, "Prozor", StringComparison.OrdinalIgnoreCase))
                {
                    deletedFaces += DeleteTerrainFacesByWindow(ed, db);
                    edgeIndex = null; // indeks vise nije validan posle prozora
                    continue;
                }

                if (pick.Status != PromptStatus.OK)
                {
                    break;
                }

                using var tr = db.TransactionManager.StartTransaction();
                edgeIndex ??= TerrainFaceEdgeIndex.Build(tr, db);
                if (edgeIndex.FaceCount == 0)
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Nema 3DFACE terena. Prvo TCMTERFACE.");
                    tr.Commit();
                    break;
                }

                if (!edgeIndex.TryFindEdgeNear(
                        pick.Value, SwapEdgePickTolerance, out var edgeA, out var edgeB, out var faceIds))
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Nema ivice unutar 1 jedinice od klika.");
                    tr.Commit();
                    continue;
                }

                var erased = 0;
                foreach (var faceId in faceIds)
                {
                    if (faceId.IsNull ||
                        tr.GetObject(faceId, OpenMode.ForWrite) is not Face face ||
                        face.IsErased)
                    {
                        continue;
                    }

                    face.Erase();
                    erased++;
                }

                if (erased > 0)
                {
                    TerrainDefinitionStore.AddDeletedEdge(tr, db, edgeA, edgeB);
                    edgeIndex.RemoveFaces(faceIds);
                }

                tr.Commit();
                deletedFaces += erased;
                ed.WriteMessage(
                    erased > 0
                        ? $"\nTCM-ROADS: Obrisano {erased} × 3DFACE (ivica sacuvana za TCMTERFACE)."
                        : "\nTCM-ROADS: Nista nije obrisano.");
            }

            if (deletedFaces > 0)
            {
                ed.WriteMessage(
                    $"\nTCM-ROADS: Ukupno obrisano 3DFACE: {deletedFaces}.");
                if (RefreshContoursIfDrawn(writeMessage: false))
                {
                    ed.WriteMessage(" Izohipse osvezene.");
                }
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static int DeleteTerrainFacesByWindow(Editor ed, Database db)
    {
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nIzaberite 3DFACE terena za brisanje (prozor/crossing): "
        };
        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "3DFACE")
        });

        var selection = ed.GetSelection(options, filter);
        if (selection.Status != PromptStatus.OK || selection.Value is null)
        {
            return 0;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var erased = 0;
        var deletedEdges = new List<(Point3d A, Point3d B)>();
        foreach (SelectedObject selected in selection.Value)
        {
            if (selected?.ObjectId.IsNull != false)
            {
                continue;
            }

            if (tr.GetObject(selected.ObjectId, OpenMode.ForRead) is not Face face || face.IsErased)
            {
                continue;
            }

            if (!TerrainFaceXData.IsTerrainFace(face) &&
                !TerrainLayerNames.IsBaseOrPrefixed(face.Layer, TerrainLayerName))
            {
                continue;
            }

            var verts = GetTriangleVertices(face);
            if (verts.Length >= 3)
            {
                deletedEdges.Add((verts[0], verts[1]));
                deletedEdges.Add((verts[1], verts[2]));
                deletedEdges.Add((verts[2], verts[0]));
            }

            face.UpgradeOpen();
            face.Erase();
            erased++;
        }

        if (deletedEdges.Count > 0)
        {
            TerrainDefinitionStore.AddDeletedEdges(tr, db, deletedEdges);
        }

        tr.Commit();
        if (erased > 0)
        {
            ed.WriteMessage($"\nTCM-ROADS: Obrisano {erased} × 3DFACE (prozor, edit sacuvan).");
        }

        return erased;
    }

    /// <summary>Javni ulaz za restore TIN-a posle brisanja granice.</summary>
    internal static IReadOnlyList<(Point3d A, Point3d B)> RebuildTerrainFacesPublic(
        Editor ed,
        Database db,
        bool announce,
        string? surfaceName = null,
        IProgress<(int Percent, string Status)>? progress = null) =>
        RebuildTerrainFaces(ed, db, announce, surfaceName, progress);

    private static IReadOnlyList<(Point3d A, Point3d B)> RebuildTerrainFaces(
        Editor ed,
        Database db,
        bool announce,
        string? surfaceName = null,
        IProgress<(int Percent, string Status)>? progress = null)
    {
        using var writeTr = db.TransactionManager.StartTransaction();
        progress?.Report((54, "Ucitavam tacke terena…"));
        var points = TerrainPointStore.Load(writeTr, db).ToList();
        if (points.Count < 3)
        {
            writeTr.Commit();
            return Array.Empty<(Point3d A, Point3d B)>();
        }

        string? resolvedName = null;
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            resolvedName = NamedTerrainSurfaceStore.NormalizeName(surfaceName);
            if (NamedTerrainSurfaceStore.IsBoundaryCompanionName(resolvedName))
            {
                resolvedName = resolvedName[..^("_Granica".Length)];
            }

            NamedTerrainSurfaceStore.SaveSurface(writeTr, db, resolvedName, points);
        }
        else
        {
            resolvedName = NamedTerrainSurfaceStore.GetActiveName(writeTr, db);
            if (NamedTerrainSurfaceStore.IsBoundaryCompanionName(resolvedName))
            {
                resolvedName = resolvedName![..^("_Granica".Length)];
                if (string.IsNullOrWhiteSpace(resolvedName))
                {
                    resolvedName = "Teren_1";
                }
            }

            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                var loaded = NamedTerrainSurfaceStore.TryLoadSurface(writeTr, db, resolvedName!);
                if (loaded is { Count: >= 3 })
                {
                    points = loaded.ToList();
                }
            }
        }

        var built = TerrainTinConstraints.Build(writeTr, db, points, progress: progress);
        if (built.Triangles.Count == 0)
        {
            ed.WriteMessage(
                "\nTCM-ROADS: Triangulacija nije uspela (tacke / granica / breakline).");
            writeTr.Commit();
            return Array.Empty<(Point3d A, Point3d B)>();
        }

        // Bazni teren ostaje netaknut; tačke granice → poseban skup *_Granica.
        var baseSurfaceName = string.IsNullOrWhiteSpace(resolvedName)
            ? "Teren_1"
            : (NamedTerrainSurfaceStore.IsBoundaryCompanionName(resolvedName)
                ? resolvedName![..^("_Granica".Length)]
                : resolvedName!);
        if (string.IsNullOrWhiteSpace(baseSurfaceName))
        {
            baseSurfaceName = "Teren_1";
        }

        var granicaName = NamedTerrainSurfaceStore.BoundaryCompanionName(baseSurfaceName);
        TerrainBoundaryPointDrawer.SyncResult? boundSync = null;
        if (built.BoundaryPoints.Count > 0)
        {
            NamedTerrainSurfaceStore.SaveSurface(
                writeTr, db, granicaName, built.BoundaryPoints, setActive: false);
        }
        else
        {
            NamedTerrainSurfaceStore.DeleteSurface(writeTr, db, granicaName);
        }

        // Radni skup = samo bazne tačke (ne mešati granicu).
        TerrainPointStore.Save(writeTr, db, points);
        if (!string.IsNullOrWhiteSpace(resolvedName) &&
            !NamedTerrainSurfaceStore.IsBoundaryCompanionName(resolvedName))
        {
            NamedTerrainSurfaceStore.SetActiveName(writeTr, db, resolvedName!);
        }

        RoadDrawing.EnsureRegApp(writeTr, db);
        var faceLayer = TerrainLayerNames.For(TerrainLayerName, resolvedName);
        var borderLayerBase = TerrainLayerNames.For(TerrainBorderLayerName, resolvedName);
        EnsureNamedLayer(writeTr, db, faceLayer, 4);
        ContourPreferences.Load();
        var borderComp = ContourPreferences.Current.GetComponent("Border");
        EnsureNamedLayer(writeTr, db, borderLayerBase,
            borderComp.ColorByLayer ? (short)2 : borderComp.ColorAci);

        progress?.Report((94, "Brisem stare 3DFACE…"));
        var erased = EraseTerrainFaces(writeTr, db, resolvedName);
        var erasedBorders = EraseTerrainBorders(writeTr, db, resolvedName);

        var modelSpace = (BlockTableRecord)writeTr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        boundSync = TerrainBoundaryPointDrawer.Sync(
            writeTr,
            db,
            modelSpace,
            granicaName,
            built.BoundaryPoints);

        progress?.Report((96, $"Crtam {built.Triangles.Count} × 3DFACE…"));
        var created = 0;
        foreach (var tri in built.Triangles)
        {
            AppendTerrainFace(
                writeTr,
                modelSpace,
                built.Vertices[tri.A],
                built.Vertices[tri.B],
                built.Vertices[tri.C],
                resolvedName);
            created++;
            if (progress is not null && built.Triangles.Count > 0 &&
                created % Math.Max(1, built.Triangles.Count / 20) == 0)
            {
                var pct = 96 + (int)(3.0 * created / built.Triangles.Count);
                progress.Report((Math.Min(99, pct), $"Crtam 3DFACE {created}/{built.Triangles.Count}…"));
            }
        }

        var borderDrawn = 0;
        if (ContourPreferences.Current.DisplayExteriorBorders && borderComp.Visible)
        {
            borderDrawn = AppendTerrainBorder(
                writeTr,
                modelSpace,
                built.Vertices,
                built.Triangles,
                resolvedName,
                borderComp);
        }

        writeTr.Commit();

        if (announce)
        {
            ed.WriteMessage(
                $"\nTCM-ROADS: Kreiran 3D teren" +
                (resolvedName is not null ? $" „{resolvedName}“" : string.Empty) +
                $" — {created} × 3DFACE na layeru {TerrainLayerNames.For(TerrainLayerName, resolvedName)} " +
                $"(od {built.Vertices.Count} tacaka" +
                (borderDrawn > 0 ? $", border {borderDrawn} ivica" : string.Empty) +
                (built.BreaklineSegments > 0 ? $", breakline seg. {built.BreaklineSegments}" : string.Empty) +
                (built.ForcedApplied > 0 ? $", fors. ivica {built.ForcedApplied}" : string.Empty) +
                (built.DeletedRemoved > 0 ? $", uklonjeno edit {built.DeletedRemoved}" : string.Empty) +
                (built.BoundaryCulled > 0 ? $", granica -{built.BoundaryCulled}" : string.Empty) +
                (erased > 0 ? $", obrisano starih {erased}" : string.Empty) +
                (erasedBorders > 0 ? $", stari border {erasedBorders}" : string.Empty) +
                ").");
            if (built.BoundaryPoints.Count > 0 && boundSync is not null)
            {
                ed.WriteMessage(
                    $"\nTCM-ROADS: Dodato {boundSync.Drawn} tacaka na granici → „{granicaName}“ " +
                    $"({boundSync.StyleLabel}). Bazni teren „{baseSurfaceName}“ nepromenjen. " +
                    "TIN koristi oba skupa.");
                if (!boundSync.UsedBlock)
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Stil bloka nije snimljen — koriscen DBPoint. " +
                        "Pokrenite TCMTERBLOK da definisete tip tacke (npr. TACKA/KOTAC).");
                }
            }
        }

        if (RefreshContoursIfDrawn(writeMessage: false))
        {
            ed.WriteMessage("\nTCM-ROADS: Izohipse osvezene (TIN promenjen).");
        }

        return built.FailedForcedEdges;
    }

    private static int AppendTerrainBorder(
        Transaction tr,
        BlockTableRecord modelSpace,
        IReadOnlyList<Point3d> vertices,
        IReadOnlyList<TerrainDelaunay.Triangle> triangles,
        string? surfaceName,
        SurfaceComponentStyle borderComp)
    {
        var ring = TerrainBorderBuilder.BuildOuterRing(vertices, triangles);
        if (ring.Count < 3)
        {
            return 0;
        }

        var pl = new Polyline(ring.Count + 1) { Closed = true };
        // Elevation = prosjek Z prstena (Civil border na surface elevaciji).
        var elev = ring.Average(p => p.Z);
        pl.Elevation = elev;
        for (var i = 0; i < ring.Count; i++)
        {
            pl.AddVertexAt(i, new Point2d(ring[i].X, ring[i].Y), 0, 0, 0);
        }

        var layer = string.IsNullOrWhiteSpace(borderComp.Layer) || borderComp.Layer == "0"
            ? TerrainLayerNames.For(TerrainBorderLayerName, surfaceName)
            : TerrainLayerNames.For(borderComp.Layer.Trim(), surfaceName);
        EnsureNamedLayer(tr, modelSpace.Database, layer,
            borderComp.ColorByLayer ? (short)2 : borderComp.ColorAci);
        pl.Layer = layer;
        if (borderComp.ColorByLayer)
        {
            pl.Color = AcColor.FromColorIndex(ColorMethod.ByLayer, 0);
        }
        else if (borderComp.ColorByBlock)
        {
            pl.Color = AcColor.FromColorIndex(ColorMethod.ByBlock, 0);
        }
        else
        {
            pl.Color = AcColor.FromColorIndex(ColorMethod.ByAci, borderComp.ColorAci);
        }

        pl.Visible = borderComp.Visible;
        modelSpace.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        TerrainBorderXData.Attach(pl, surfaceName);
        return ring.Count;
    }

    private static int EraseTerrainBorders(Transaction tr, Database db, string? surfaceName = null)
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

            // Samo TCM border sa XData — ne briši korisničku granicu (TCMTERBOUND) bez XData.
            if (!TerrainBorderXData.IsTerrainBorder(entity))
            {
                continue;
            }

            if (!TerrainSurfaceScope.BorderBelongsTo(entity, surfaceName))
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Erase();
            count++;
        }

        return count;
    }

    private static void EnsureNamedLayer(Transaction tr, Database db, string name, short aci)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(name))
        {
            var existing = (LayerTableRecord)tr.GetObject(layerTable[name], OpenMode.ForWrite);
            existing.Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci);
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci)
        };
        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static ObjectId AppendTerrainFace(
        Transaction tr,
        BlockTableRecord modelSpace,
        Point3d a,
        Point3d b,
        Point3d c,
        string? surfaceName = null)
    {
        var face = new Face(a, b, c, c, true, true, true, true)
        {
            Layer = TerrainLayerNames.For(TerrainLayerName, surfaceName)
        };
        modelSpace.AppendEntity(face);
        tr.AddNewlyCreatedDBObject(face, true);
        TerrainFaceXData.Attach(face, surfaceName);
        return face.ObjectId;
    }

    private static int EraseTerrainFaces(Transaction tr, Database db, string? surfaceName = null)
    {
        var count = 0;
        foreach (var face in LoadTerrainFaces(tr, db, surfaceName))
        {
            face.UpgradeOpen();
            face.Erase();
            count++;
        }

        return count;
    }

    private static int CountTerrainFaces(Transaction tr, Database db) =>
        LoadTerrainFaces(tr, db).Count;

    private static List<Face> LoadTerrainFaces(Transaction tr, Database db, string? surfaceName = null)
    {
        var result = new List<Face>();
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Face face || face.IsErased)
            {
                continue;
            }

            if (!TerrainSurfaceScope.FaceBelongsTo(face, surfaceName))
            {
                continue;
            }

            result.Add(face);
        }

        return result;
    }

    /// <summary>Najbliza ivica bilo kog TCM trougla (Civil Delete Line pick).</summary>
    private static bool TryFindEdgeNear(
        Point3d pick,
        IReadOnlyList<Face> faces,
        double tolerance,
        out Point3d edgeA,
        out Point3d edgeB)
    {
        edgeA = default;
        edgeB = default;
        var bestDist = tolerance;
        var found = false;

        foreach (var face in faces)
        {
            if (face.IsErased)
            {
                continue;
            }

            var verts = GetTriangleVertices(face);
            for (var e = 0; e < 3; e++)
            {
                var a = verts[e];
                var b = verts[(e + 1) % 3];
                var dist = DistancePointToSegmentXy(pick, a, b);
                if (dist > bestDist)
                {
                    continue;
                }

                bestDist = dist;
                edgeA = a;
                edgeB = b;
                found = true;
            }
        }

        return found;
    }

    private static bool FaceHasEdge(Face face, Point3d edgeA, Point3d edgeB)
    {
        var verts = GetTriangleVertices(face);
        var hasA = false;
        var hasB = false;
        foreach (var v in verts)
        {
            if (SameVertex(v, edgeA))
            {
                hasA = true;
            }
            else if (SameVertex(v, edgeB))
            {
                hasB = true;
            }
        }

        return hasA && hasB;
    }

    private static bool TryFindSharedEdgeNear(
        Point3d pick,
        IReadOnlyList<Face> faces,
        double tolerance,
        out (Face Face1, Face Face2, Point3d A, Point3d B, Point3d C, Point3d D) pair)
    {
        pair = default;
        var bestDist = tolerance;
        var found = false;
        (Face, Face, Point3d, Point3d, Point3d, Point3d) best = default;

        for (var i = 0; i < faces.Count; i++)
        {
            var verts1 = GetTriangleVertices(faces[i]);
            for (var e = 0; e < 3; e++)
            {
                var a = verts1[e];
                var b = verts1[(e + 1) % 3];
                var c = verts1[(e + 2) % 3];
                var dist = DistancePointToSegmentXy(pick, a, b);
                if (dist > bestDist)
                {
                    continue;
                }

                for (var j = 0; j < faces.Count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    var verts2 = GetTriangleVertices(faces[j]);
                    if (!TryGetOppositeVertex(verts2, a, b, out var d))
                    {
                        continue;
                    }

                    // Konveksan četvorougao: C i D sa suprotnih strana AB, A i B sa suprotnih strana CD.
                    if (!IsConvexSwap(a, b, c, d))
                    {
                        continue;
                    }

                    bestDist = dist;
                    best = (faces[i], faces[j], a, b, c, d);
                    found = true;
                }
            }
        }

        if (!found)
        {
            return false;
        }

        pair = best;
        return true;
    }

    private static Point3d[] GetTriangleVertices(Face face) =>
        new[]
        {
            face.GetVertexAt(0),
            face.GetVertexAt(1),
            face.GetVertexAt(2)
        };

    private static bool TryGetOppositeVertex(
        Point3d[] verts,
        Point3d edgeA,
        Point3d edgeB,
        out Point3d opposite)
    {
        opposite = default;
        var hasA = false;
        var hasB = false;
        Point3d? other = null;

        foreach (var v in verts)
        {
            if (SameVertex(v, edgeA))
            {
                hasA = true;
            }
            else if (SameVertex(v, edgeB))
            {
                hasB = true;
            }
            else
            {
                other = v;
            }
        }

        if (!hasA || !hasB || other is null)
        {
            return false;
        }

        opposite = other.Value;
        return true;
    }

    private static bool SameVertex(Point3d a, Point3d b) =>
        a.DistanceTo(b) <= 1e-6;

    /// <summary>
    /// Civil kriterijum: cetvorougao od dva trougla koji dele AB mora biti konveksan
    /// da bi se dijagonala mogla zameniti sa CD.
    /// </summary>
    private static bool IsConvexSwap(Point3d a, Point3d b, Point3d c, Point3d d)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var crossC = abx * (c.Y - a.Y) - aby * (c.X - a.X);
        var crossD = abx * (d.Y - a.Y) - aby * (d.X - a.X);
        if (crossC * crossD >= 0)
        {
            return false;
        }

        var cdx = d.X - c.X;
        var cdy = d.Y - c.Y;
        var crossA = cdx * (a.Y - c.Y) - cdy * (a.X - c.X);
        var crossB = cdx * (b.Y - c.Y) - cdy * (b.X - c.X);
        return crossA * crossB < 0;
    }

    private static double DistancePointToSegmentXy(Point3d p, Point3d a, Point3d b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        if (len2 < 1e-18)
        {
            var dx0 = p.X - a.X;
            var dy0 = p.Y - a.Y;
            return Math.Sqrt(dx0 * dx0 + dy0 * dy0);
        }

        var t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2;
        t = Math.Max(0, Math.Min(1, t));
        var qx = a.X + t * abx;
        var qy = a.Y + t * aby;
        var dx = p.X - qx;
        var dy = p.Y - qy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static List<TerrainPointVm> CollectTerrainPointsWithIds(
        Editor ed,
        Database db,
        bool requireThreeNew)
    {
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nIzaberite 3D tacke (POINT) za teren: "
        };
        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "POINT")
        });

        var selection = ed.GetSelection(options, filter);
        var points = new List<TerrainPointVm>();

        if (selection.Status == PromptStatus.OK && selection.Value is not null)
        {
            using var tr = db.TransactionManager.StartTransaction();
            foreach (SelectedObject selected in selection.Value)
            {
                if (selected?.ObjectId.IsNull != false)
                {
                    continue;
                }

                if (tr.GetObject(selected.ObjectId, OpenMode.ForRead) is DBPoint dbPoint)
                {
                    var p = dbPoint.Position;
                    points.Add(new TerrainPointVm(p.X, p.Y, p.Z, dbPoint.Handle.Value));
                }
            }

            tr.Commit();
        }

        if (requireThreeNew && points.Count >= 3)
        {
            return DeduplicateVm(points);
        }

        if (!requireThreeNew && points.Count > 0)
        {
            return DeduplicateVm(points);
        }

        ed.WriteMessage(
            points.Count == 0
                ? "\nNema POINT objekata — odredite tacke rucno (Enter = kraj)."
                : requireThreeNew
                    ? $"\nJos {3 - points.Count} tacke minimum — nastavite rucnim izborom (Enter = kraj)."
                    : "\nNastavite rucnim izborom (Enter = kraj).");

        while (true)
        {
            var prompt = new PromptPointOptions("\nTacka terena (Enter = kraj): ")
            {
                AllowNone = true
            };
            var result = ed.GetPoint(prompt);
            if (result.Status != PromptStatus.OK)
            {
                break;
            }

            var p = result.Value;
            points.Add(new TerrainPointVm(p.X, p.Y, p.Z));
        }

        return DeduplicateVm(points);
    }

    private static List<Point3d> DeduplicateXy(IReadOnlyList<Point3d> points)
    {
        var unique = new List<Point3d>(points.Count);
        const double tol = 1e-8;
        foreach (var p in points)
        {
            var isDup = false;
            for (var i = 0; i < unique.Count; i++)
            {
                var q = unique[i];
                if (Math.Abs(p.X - q.X) <= tol && Math.Abs(p.Y - q.Y) <= tol)
                {
                    isDup = true;
                    break;
                }
            }

            if (!isDup)
            {
                unique.Add(p);
            }
        }

        return unique;
    }

    private static void EnsureTerrainLayer(Transaction tr, Database db)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(TerrainLayerName))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = TerrainLayerName,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, 3)
        };
        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }
}
