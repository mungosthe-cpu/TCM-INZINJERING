using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.Profile;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Podužni profil (CGSA Plateia stil Faza 1): Unos terena → grafik + banderola.
/// </summary>
public sealed partial class RoadCommands
{
    [CommandMethod("TCMPODCRT", CommandFlags.Modal)]
    public void DrawLongitudinalProfileFull()
    {
        DrawProfileCore(includeTerrainInMessage: true);
    }

    [CommandMethod("TCMPODTAB", CommandFlags.Modal)]
    public void DrawLongitudinalProfileTable()
    {
        // Samo tabela + mreža + niveleta (bez linije terena) — razlikuje se od TCMPODCRT.
        DrawProfileCore(includeTerrainInMessage: false, drawTerrain: false);
    }

    [CommandMethod("TCMPODTER", CommandFlags.Modal)]
    public void DrawLongitudinalProfileTerrain()
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
            var peo = new PromptEntityOptions("\nIzaberite okvir tabele poduznog profila: ")
            {
                AllowNone = false
            };
            peo.SetRejectMessage("\nIzaberite polyliniju okvira profila.");
            peo.AddAllowedClass(typeof(Polyline), exactMatch: false);

            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                return;
            }

            ProfileViewData view;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (tr.GetObject(per.ObjectId, OpenMode.ForRead) is not Entity entity ||
                    !TryResolveProfileView(tr, db, entity, out view!))
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Objekat nije TCM tabela profila. Prvo TCMPODCRT.");
                    tr.Commit();
                    return;
                }

                tr.Commit();
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!ProfileProjectedSampler.TryLoadSamples(tr, db, view.AxisName, out var samples))
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Nema projektovane 3D nivelete. Prvo TCMPROJTER.");
                    tr.Commit();
                    return;
                }

                ProfileDrawing.EraseProfileEntities(tr, db, view.ProfileId, ProfileXData.RoleTerrain);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);
                var id = ProfileDrawing.DrawTerrainPolyline(tr, modelSpace, view, samples);
                tr.Commit();

                ed.WriteMessage(
                    id.IsNull
                        ? "\nTCM-ROADS: Linija terena nije nacrtana."
                        : $"\nTCM-ROADS: Teren u profilu — {samples.Count} tacaka.");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static void DrawProfileCore(bool includeTerrainInMessage, bool drawTerrain = true)
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

            List<(double Station, double Elevation)> samples;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!ProfileProjectedSampler.TryLoadSamples(tr, db, axisName, out samples))
                {
                    ed.WriteMessage(
                        "\nTCM-ROADS: Nema projektovane 3D nivelete. Prvo TCMPROJTER.");
                    tr.Commit();
                    return;
                }

                tr.Commit();
            }

            var minZ = samples.Min(s => s.Elevation);
            var maxZ = samples.Max(s => s.Elevation);
            double crossInterval = 20;
            using (var trMeta = db.TransactionManager.StartTransaction())
            {
                var meta = RoadAxisStore.Load(trMeta, db, axisName);
                if (meta is not null && meta.Interval > 1e-6)
                {
                    crossInterval = meta.Interval;
                }

                trMeta.Commit();
            }

            if (!TryPromptProfileTerrainOptions(
                    axisName,
                    axis.StartStation,
                    axis.StartStation + axis.TotalLength,
                    minZ,
                    maxZ,
                    crossInterval,
                    out var opts))
            {
                return;
            }

            var insert = ed.GetPoint(new PromptPointOptions(
                "\nUnosna tacka (donji levi ugao tabele profila): "));
            if (insert.Status != PromptStatus.OK)
            {
                return;
            }

            var profileId = Guid.NewGuid().ToString("N")[..8];
            var view = new ProfileViewData
            {
                ProfileId = profileId,
                AxisName = axisName,
                TableName = opts.TableName,
                TableType = string.IsNullOrWhiteSpace(opts.TableType) ? "TCM_1" : opts.TableType,
                Origin = insert.Value,
                StartStation = opts.StartStation,
                EndStation = opts.EndStation,
                BaseElevation = opts.BaseElevation,
                TopElevation = opts.TopElevation,
                HorizontalDenom = opts.HorizontalDenom,
                VerticalDenom = opts.VerticalDenom,
                StationTickInterval = opts.StationInterval,
                TabulationMode = opts.TabulationMode,
                CrossAxisInterval = opts.CrossAxisInterval,
                BetweenDivisor = opts.BetweenDivisor,
                DrawVerticals = opts.DrawVerticals,
                DrawTabulation = opts.DrawTabulation
            };

            using (var tr = db.TransactionManager.StartTransaction())
            {
                ProfileViewStore.Save(tr, db, view);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);
                ProfileDrawing.DrawFullProfile(tr, modelSpace, view, samples, drawTerrain);
                tr.Commit();
            }

            ed.WriteMessage(
                includeTerrainInMessage
                    ? $"\nTCM-ROADS: Poduzni profil '{opts.TableName}' — tabela + teren ({samples.Count} tacaka)."
                    : $"\nTCM-ROADS: Tabela profila '{opts.TableName}' (bez linije terena). Dodajte TCMPODTER ili TCMPODCRT.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static bool TryPromptProfileTerrainOptions(
        string axisName,
        double startStation,
        double endStation,
        double minZ,
        double maxZ,
        double crossAxisInterval,
        out ProfileTerrainDialogResult opts)
    {
        opts = null!;

#if BRICSCAD
        return TryPromptProfileTerrainCli(
            AcApp.DocumentManager.MdiActiveDocument?.Editor,
            axisName, startStation, endStation, minZ, maxZ, crossAxisInterval, out opts);
#else
        try
        {
            var dialog = new ProfileTerrainDialog(
                axisName, startStation, endStation, minZ, maxZ, crossAxisInterval);
            if (AcApp.ShowModalWindow(dialog) != true || dialog.Result is null)
            {
                return false;
            }

            opts = dialog.Result;
            return true;
        }
        catch (System.Exception ex)
        {
            var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\nTCM-ROADS: Dijalog Unos terena nije otvoren: {ex.Message}");
            return false;
        }
#endif
    }

    private static bool TryPromptProfileTerrainCli(
        Editor? ed,
        string axisName,
        double startStation,
        double endStation,
        double minZ,
        double maxZ,
        double crossAxisInterval,
        out ProfileTerrainDialogResult opts)
    {
        opts = null!;
        if (ed is null)
        {
            return false;
        }

        var cross = crossAxisInterval > 1e-6 ? crossAxisInterval : 20;
        var h = PromptDouble(ed, "\nHorizontalna razmera 1:<1000>: ", 1000);
        if (h is null)
        {
            return false;
        }

        var v = PromptDouble(ed, "\nVertikalna razmera 1:<100>: ", 100);
        if (v is null)
        {
            return false;
        }

        var baseElev = PromptDouble(ed, $"\nReferentna visina <{Math.Floor(minZ):0.###}>: ", Math.Floor(minZ));
        if (baseElev is null)
        {
            return false;
        }

        var topElev = PromptDouble(ed, $"\nVisina vrha profila <{Math.Ceiling(maxZ + 1):0.###}>: ", Math.Ceiling(maxZ + 1));
        if (topElev is null)
        {
            return false;
        }

        opts = new ProfileTerrainDialogResult
        {
            TableName = $"PROFIL-1: {axisName}",
            TableType = "TCM_1",
            HorizontalDenom = h.Value,
            VerticalDenom = v.Value,
            StartStation = startStation,
            EndStation = endStation,
            BaseElevation = baseElev.Value,
            TopElevation = topElev.Value,
            StationInterval = cross,
            TabulationMode = ProfileTabulationMode.CrossAxes,
            CrossAxisInterval = cross,
            BetweenDivisor = 2,
            DrawTabulation = true,
            DrawVerticals = true
        };
        return true;
    }

    private static bool TryResolveProfileView(
        Transaction tr,
        Database db,
        Entity entity,
        out ProfileViewData? view)
    {
        view = null;
        if (ProfileXData.TryReadView(entity, out var profileId, out _))
        {
            view = ProfileViewStore.Load(tr, db, profileId);
            return view is not null;
        }

        if (ProfileXData.TryReadRole(entity, out _, out profileId))
        {
            view = ProfileViewStore.Load(tr, db, profileId);
            return view is not null;
        }

        return false;
    }

    private static bool TryPickAxis(Editor ed, Database db, out string axisName, out RoadAxis axis)
    {
        axisName = string.Empty;
        axis = null!;

        var options = new PromptEntityOptions(
            "\nIzaberite osovinu ili 3D projektovanu niveletu: ")
        {
            AllowNone = false
        };
        options.SetRejectMessage("\nIzaberite Line, Arc, LWPOLYLINE ili 3D polyliniju.");
        options.AddAllowedClass(typeof(Line), exactMatch: false);
        options.AddAllowedClass(typeof(Arc), exactMatch: false);
        options.AddAllowedClass(typeof(Polyline), exactMatch: false);
        options.AddAllowedClass(typeof(Polyline3d), exactMatch: false);
        var result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
        {
            return false;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var entity = tr.GetObject(result.ObjectId, OpenMode.ForRead) as Entity;
        if (entity is null)
        {
            tr.Commit();
            return false;
        }

        if (RoadXData.TryReadSourcePolyline(entity, out axisName) ||
            RoadXData.TryReadAxisElement(entity, out axisName, out _) ||
            RoadXData.TryReadProjectedAxis(entity, out axisName))
        {
            var metadata = RoadAxisStore.Load(tr, db, axisName);
            var startStation = metadata?.StartStation ?? 0;
            var loaded = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
            tr.Commit();
            if (loaded is null || loaded.Elements.Count == 0)
            {
                ed.WriteMessage("\nTCM-ROADS: Osovina nije dostupna u memoriji.");
                return false;
            }

            axis = loaded;
            return true;
        }

        tr.Commit();
        ed.WriteMessage("\nTCM-ROADS: Nije TCM osovina / projekcija.");
        return false;
    }
}
