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
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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
                ed.WriteMessage("\nTCM-INZINJERING: Nije izabran validan blok.");
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
                $"\nTCM-INZINJERING: Blok „{blockName}“ nema atribute. Potreban je atribut sa visinom.");
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

        ed.WriteMessage(
            $"\nTCM-INZINJERING: Obeležite instance bloka „{mapping.BlockName}“ " +
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
                ed.WriteMessage("\nTCM-INZINJERING: Nista nije izabrano.");
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
                    "\nTCM-INZINJERING: Nijedna tacka nije izvucena iz blokova" +
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
                $"\nTCM-INZINJERING: Dodato {collected.Count} tacaka iz bloka „{mapping.BlockName}“" +
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
                ed.WriteMessage("\nTCM-INZINJERING: Nije izabrana nijedna tacka.");
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
                        $"\nTCM-INZINJERING: Potrebne su najmanje 3 tacke (ukupno {working.Count}).");
                    tr.Commit();
                    return;
                }

                PersistAndSyncPoints(tr, db, working, eraseHandles: null);
                hadFaces = CountTerrainFaces(tr, db) > 0;
                tr.Commit();
            }

            ed.WriteMessage(
                $"\nTCM-INZINJERING: Sacuvano {working.Count} tacaka za teren" +
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
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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
                "\nTCM-INZINJERING: Nema sacuvanih tacaka. Koristite Izaberi tacke / Dodaj tacke / Ucitaj tacke.");
            return;
        }

        ShowTerrainPointsSummary(working, appendMode: true, rebuiltTin: hadFaces);
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
            ed.WriteMessage($"\nTCM-INZINJERING: Ne mogu da ucitam fajl: {ex.Message}");
            return;
        }

        if (loaded.Count == 0)
        {
            ed.WriteMessage("\nTCM-INZINJERING: Fajl ne sadrzi validne X,Y,Z tacke.");
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

        ed.WriteMessage($"\nTCM-INZINJERING: Ucitano {working.Count} tacaka. Zatim TCMTERFACE za TIN.");
        ShowTerrainPointsSummary(working, appendMode: true, rebuiltTin: false);
#else
        ed.WriteMessage("\nTCM-INZINJERING: Ucitavanje fajla nije dostupno u ovoj konfiguraciji.");
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
            ed.WriteMessage("\nTCM-INZINJERING: Nema tacaka za snimanje.");
            return;
        }

        try
        {
            var path = ExportTerrainPointsToProject(rows);
            ed.WriteMessage(path is null
                ? "\nTCM-INZINJERING: Nema tacaka za snimanje."
                : $"\nTCM-INZINJERING: Tacke snimljene:\n{path}");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING: Greska pri snimanju: {ex.Message}");
        }
    }

    private static void ShowTerrainPointsSummary(
        IReadOnlyList<TerrainPointVm> points,
        bool appendMode,
        bool rebuiltTin,
        string? statusHint = null)
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
                savePointsToProject: rows =>
                {
                    try
                    {
                        return ExportTerrainPointsToProject(rows);
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
                listNamedTerrains: () =>
                {
                    using var tr = doc.Database.TransactionManager.StartTransaction();
                    var names = NamedTerrainSurfaceStore.ListNames(tr, doc.Database);
                    var active = NamedTerrainSurfaceStore.GetActiveName(tr, doc.Database);
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

                        tr.Commit();
                        var vms = pts.Select(p => new TerrainPointVm(p.X, p.Y, p.Z, isAdded: true))
                            .ToList();
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
                $"\nTCM-INZINJERING: Pregled tacaka nije otvoren: {ex.Message}");
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
            doc.Editor.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var faces = LoadTerrainFaces(tr, db);
                    if (faces.Count == 0)
                    {
                        ed.WriteMessage(
                            "\nTCM-INZINJERING: Nema 3DFACE terena. Prvo TCMTERFACE.");
                        tr.Commit();
                        return;
                    }

                    if (!TrySnapToTinVertex(first.Value, faces, SwapEdgePickTolerance, out var a) ||
                        !TrySnapToTinVertex(second.Value, faces, SwapEdgePickTolerance, out var b))
                    {
                        ed.WriteMessage(
                            "\nTCM-INZINJERING: Kliknite blizu postojecih TIN temena (kao Civil Add Line).");
                        tr.Commit();
                        continue;
                    }

                    if (XyDistance(a, b) < 1e-6)
                    {
                        ed.WriteMessage("\nTCM-INZINJERING: Temena moraju biti razlicita.");
                        tr.Commit();
                        continue;
                    }

                    if (!TryCommitAddLineSegment(tr, db, a, b))
                    {
                        tr.Commit();
                        continue;
                    }

                    tr.Commit();
                }

                RebuildTerrainFaces(ed, db, announce: false);
                added++;
                ed.WriteMessage(
                    "\nTCM-INZINJERING: Add Line — ivica forsira i TIN osvezen (sacuvano za TCMTERFACE).");
            }

            if (added > 0)
            {
                ed.WriteMessage($"\nTCM-INZINJERING: Ukupno Add Line: {added}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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
                            "\nTCM-INZINJERING: Nema 3DFACE terena. Prvo TCMTERFACE.");
                        tr.Commit();
                        return;
                    }

                    if (!TrySnapToTinVertex(start.Value, faces, SwapEdgePickTolerance, out current))
                    {
                        ed.WriteMessage(
                            "\nTCM-INZINJERING: Kliknite blizu postojeceg TIN temena.");
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
                                "\nTCM-INZINJERING: Kliknite blizu postojeceg TIN temena.");
                            tr.Commit();
                            continue;
                        }

                        if (XyDistance(current, next) < 1e-6)
                        {
                            ed.WriteMessage("\nTCM-INZINJERING: Temena moraju biti razlicita.");
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
                        "\nTCM-INZINJERING: Add Continuous Line — segment forsiran, TIN osvezen.");
                }

                if (chainSegments > 0)
                {
                    ed.WriteMessage(
                        $"\nTCM-INZINJERING: Lanac zavrsen ({chainSegments} segment(a)).");
                }
            }

            if (added > 0)
            {
                ed.WriteMessage($"\nTCM-INZINJERING: Ukupno Add Continuous Line: {added}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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
            ed.WriteMessage("\nTCM-INZINJERING: Linija nema dovoljno temena.");
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
                ed.WriteMessage("\nTCM-INZINJERING: Nema segmenata za Add Line.");
                return 0;
            }

            if (!TerrainTinConstraints.TryValidateForcedEdges(tr, db, points, candidateEdges, out var failure))
            {
                tr.Commit();
                ShowAddLineBlockedMessage(failure);
                return 0;
            }

            TerrainPointStore.Save(tr, db, points);
            foreach (var edge in candidateEdges)
            {
                TerrainDefinitionStore.AddForcedEdge(tr, db, edge.A, edge.B);
                segments++;
            }

            tr.Commit();
        }

        if (segments == 0)
        {
            ed.WriteMessage("\nTCM-INZINJERING: Nema segmenata za Add Line.");
            return 0;
        }

        RebuildTerrainFaces(ed, db, announce: false);
        ed.WriteMessage(
            $"\nTCM-INZINJERING: Add Line duz linije — {segments} forsiran(ih) ivica, TIN osvezen.");
        return segments;
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
            "TCM-INŽINJERING — Add Line",
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
                        "\nTCM-INZINJERING: Nema dovoljno 3DFACE terena. Prvo TCMTERFACE.");
                    tr.Commit();
                    break;
                }

                if (!TryFindSharedEdgeNear(pick.Value, faces, SwapEdgePickTolerance, out var pair))
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Nema pogodne ivice (klik unutar 1 jedinice, " +
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
                ed.WriteMessage("\nTCM-INZINJERING: Ivica zamenjena (sacuvano za TCMTERFACE).");
                if (RefreshContoursIfDrawn(writeMessage: false))
                {
                    ed.WriteMessage(" Izohipse osvezene.");
                }
            }

            if (swapped > 0)
            {
                ed.WriteMessage($"\nTCM-INZINJERING: Ukupno zamenjeno ivica: {swapped}.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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
                    continue;
                }

                if (pick.Status != PromptStatus.OK)
                {
                    break;
                }

                using var tr = db.TransactionManager.StartTransaction();
                var faces = LoadTerrainFaces(tr, db);
                if (faces.Count == 0)
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Nema 3DFACE terena. Prvo TCMTERFACE.");
                    tr.Commit();
                    break;
                }

                if (!TryFindEdgeNear(pick.Value, faces, SwapEdgePickTolerance, out var edgeA, out var edgeB))
                {
                    ed.WriteMessage(
                        "\nTCM-INZINJERING: Nema ivice unutar 1 jedinice od klika.");
                    tr.Commit();
                    continue;
                }

                var erased = 0;
                foreach (var face in faces)
                {
                    if (face.IsErased)
                    {
                        continue;
                    }

                    if (!FaceHasEdge(face, edgeA, edgeB))
                    {
                        continue;
                    }

                    face.UpgradeOpen();
                    face.Erase();
                    erased++;
                }

                if (erased > 0)
                {
                    TerrainDefinitionStore.AddDeletedEdge(tr, db, edgeA, edgeB);
                }

                tr.Commit();
                deletedFaces += erased;
                ed.WriteMessage(
                    erased > 0
                        ? $"\nTCM-INZINJERING: Obrisano {erased} × 3DFACE (ivica sacuvana za TCMTERFACE)."
                        : "\nTCM-INZINJERING: Nista nije obrisano.");
            }

            if (deletedFaces > 0)
            {
                ed.WriteMessage(
                    $"\nTCM-INZINJERING: Ukupno obrisano 3DFACE: {deletedFaces}.");
                if (RefreshContoursIfDrawn(writeMessage: false))
                {
                    ed.WriteMessage(" Izohipse osvezene.");
                }
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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
                TerrainDefinitionStore.AddDeletedEdge(tr, db, verts[0], verts[1]);
                TerrainDefinitionStore.AddDeletedEdge(tr, db, verts[1], verts[2]);
                TerrainDefinitionStore.AddDeletedEdge(tr, db, verts[2], verts[0]);
            }

            face.UpgradeOpen();
            face.Erase();
            erased++;
        }

        tr.Commit();
        if (erased > 0)
        {
            ed.WriteMessage($"\nTCM-INZINJERING: Obrisano {erased} × 3DFACE (prozor, edit sacuvan).");
        }

        return erased;
    }

    private static void RebuildTerrainFaces(
        Editor ed,
        Database db,
        bool announce,
        string? surfaceName = null)
    {
        using var writeTr = db.TransactionManager.StartTransaction();
        var points = TerrainPointStore.Load(writeTr, db).ToList();
        if (points.Count < 3)
        {
            writeTr.Commit();
            return;
        }

        string? resolvedName = null;
        if (!string.IsNullOrWhiteSpace(surfaceName))
        {
            resolvedName = NamedTerrainSurfaceStore.NormalizeName(surfaceName);
            NamedTerrainSurfaceStore.SaveSurface(writeTr, db, resolvedName, points);
        }
        else
        {
            resolvedName = NamedTerrainSurfaceStore.GetActiveName(writeTr, db);
        }

        var built = TerrainTinConstraints.Build(writeTr, db, points);
        if (built.Triangles.Count == 0)
        {
            ed.WriteMessage(
                "\nTCM-INZINJERING: Triangulacija nije uspela (tacke / granica / breakline).");
            writeTr.Commit();
            return;
        }

        RoadDrawing.EnsureRegApp(writeTr, db);
        var faceLayer = TerrainLayerNames.For(TerrainLayerName, resolvedName);
        var borderLayerBase = TerrainLayerNames.For(TerrainBorderLayerName, resolvedName);
        EnsureNamedLayer(writeTr, db, faceLayer, 4);
        ContourPreferences.Load();
        var borderComp = ContourPreferences.Current.GetComponent("Border");
        EnsureNamedLayer(writeTr, db, borderLayerBase,
            borderComp.ColorByLayer ? (short)2 : borderComp.ColorAci);

        var erased = EraseTerrainFaces(writeTr, db, resolvedName);
        var erasedBorders = EraseTerrainBorders(writeTr, db, resolvedName);

        var modelSpace = (BlockTableRecord)writeTr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

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
                $"\nTCM-INZINJERING: Kreiran 3D teren" +
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
        }

        if (RefreshContoursIfDrawn(writeMessage: false))
        {
            ed.WriteMessage("\nTCM-INZINJERING: Izohipse osvezene (TIN promenjen).");
        }
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

    private static void AppendTerrainFace(
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
