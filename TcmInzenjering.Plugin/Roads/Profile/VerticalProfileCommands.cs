using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.Profile;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    /// <summary>
    /// Kreira niveletu od postojećeg terena (TCMPROJTER) + konstantan ofset.
    /// </summary>
    [CommandMethod("TCMNIVODTER", CommandFlags.Modal)]
    public void CreateVerticalProfileFromTerrain()
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
            if (!TryChooseAxis(ed, db, out var axisName, out var axis))
            {
                return;
            }

            var offsetOpt = new PromptDoubleOptions("\nOfset nivelete od terena [m] <0.0>: ")
            {
                AllowNegative = true,
                AllowZero = true,
                AllowNone = true,
                DefaultValue = 0.0
            };
            var offsetRes = ed.GetDouble(offsetOpt);
            if (offsetRes.Status == PromptStatus.Cancel)
            {
                return;
            }

            var offset = offsetRes.Status == PromptStatus.OK ? offsetRes.Value : 0.0;

            List<(double Station, double Elevation)> terrain;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!ProfileProjectedSampler.TryLoadSamples(tr, db, axisName, out terrain) ||
                    terrain.Count < 2)
                {
                    ed.WriteMessage("\nTCM-ROADS: Nema projektovanog terena. Prvo TCMPROJTER.");
                    tr.Commit();
                    return;
                }

                tr.Commit();
            }

            // Retkiji PVI: početak, kraj + svakih ~25 m (ili sve tačke ako ih je malo).
            var pvis = new List<VerticalPvi>();
            var step = 25.0;
            var start = terrain[0].Station;
            var end = terrain[^1].Station;
            for (var s = start; s < end - 1e-3; s += step)
            {
                var z = Interpolate(terrain, s);
                if (z is not null)
                {
                    pvis.Add(new VerticalPvi { Station = s, Elevation = z.Value + offset });
                }
            }

            var zEnd = Interpolate(terrain, end);
            if (zEnd is not null)
            {
                pvis.Add(new VerticalPvi { Station = end, Elevation = zEnd.Value + offset });
            }

            if (pvis.Count < 2)
            {
                ed.WriteMessage("\nTCM-ROADS: Nedovoljno tacaka za niveletu.");
                return;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                VerticalProfileStore.Save(tr, db, axisName, pvis);
                ProfileViewRefresh.RefreshIfExists(tr, db, axisName);
                tr.Commit();
            }

            ed.WriteMessage(
                $"\nTCM-ROADS: Niveleta za '{axisName}' — {pvis.Count} PVI (ofset {offset:0.###} m).");
            ProfileViewRefresh.ScheduleIfExists(doc, axisName);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    /// <summary>Ručno dodavanje / zamena PVI na niveleti (stacionaža + kota).</summary>
    [CommandMethod("TCMNIVUREDI", CommandFlags.Modal)]
    public void EditVerticalProfilePvi()
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

            List<VerticalPvi> pvis;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                pvis = VerticalProfileStore.Load(tr, db, axisName).ToList();
                tr.Commit();
            }

            ed.WriteMessage($"\nTCM-ROADS: Niveleta '{axisName}' — {pvis.Count} PVI. Enter = kraj unosa.");

            while (true)
            {
                var stOpt = new PromptDoubleOptions("\nStacionaza PVI [m] (Enter = kraj): ")
                {
                    AllowNegative = false,
                    AllowNone = true
                };
                var stRes = ed.GetDouble(stOpt);
                if (stRes.Status != PromptStatus.OK)
                {
                    break;
                }

                var zOpt = new PromptDoubleOptions($"\nKota na {stRes.Value:0.###} [m]: ")
                {
                    AllowNegative = true,
                    AllowNone = false
                };
                var zRes = ed.GetDouble(zOpt);
                if (zRes.Status != PromptStatus.OK)
                {
                    break;
                }

                pvis.RemoveAll(p => Math.Abs(p.Station - stRes.Value) < 1e-3);
                pvis.Add(new VerticalPvi { Station = stRes.Value, Elevation = zRes.Value });
                ed.WriteMessage($"\n  PVI {stRes.Value:0.###} / {zRes.Value:0.###}");
            }

            if (pvis.Count < 2)
            {
                ed.WriteMessage("\nTCM-ROADS: Potrebna su najmanje 2 PVI. Nista nije snimljeno.");
                return;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                VerticalProfileStore.Save(tr, db, axisName, pvis);
                ProfileViewRefresh.RefreshIfExists(tr, db, axisName);
                tr.Commit();
            }

            ed.WriteMessage($"\nTCM-ROADS: Sacuvano {pvis.Count} PVI za '{axisName}'.");
            ProfileViewRefresh.ScheduleIfExists(doc, axisName);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    [CommandMethod("TCMKOLSIR", CommandFlags.Modal)]
    public void SetLaneWidths()
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
            if (!TryChooseAxis(ed, db, out var axisName, out var axis))
            {
                return;
            }

            double left = 3.5, right = 3.5;
            LaneWidthDefinitionSet definitions;
            RoadAxisMetadata? metadata;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ProfileLaneWidthStore.TryGetDefaults(tr, db, axisName, out left, out right);
                if (left < 1e-9)
                {
                    left = 3.5;
                }

                if (right < 1e-9)
                {
                    right = 3.5;
                }

                definitions = LaneWidthDefinitionStore.Load(
                    tr, db, axisName, left, right);
                metadata = RoadAxisStore.Load(tr, db, axisName);
                tr.Commit();
            }

            LaneWidthCloseAction lastAction;
            while (true)
            {
                var dialog = new LaneWidthManagerDialog(axisName, definitions);
                AcApp.ShowModalWindow(dialog);
                lastAction = dialog.CloseAction;
                definitions = dialog.Result;

                if (lastAction == LaneWidthCloseAction.Cancelled)
                {
                    return;
                }

                if (lastAction == LaneWidthCloseAction.Confirmed)
                {
                    break;
                }

                if (!AxisStationPicker.TryPickStation(
                        doc, axis, metadata, "Izaberite stacionazu na osi: ", out var station))
                {
                    continue;
                }

                var applyDialog = new LaneWidthManagerDialog(axisName, definitions);
                definitions = applyDialog.ApplyPickedStation(lastAction, station);
            }

            var midStation = axis.StartStation + axis.TotalLength * 0.5;
            var evaluated = LaneWidthEvaluator.Evaluate(definitions, axis, midStation);
            left = evaluated.LeftCarriageway;
            right = evaluated.RightCarriageway;

            var edgeCount = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                LaneWidthDefinitionStore.Save(tr, db, axisName, definitions);
                ProfileLaneWidthStore.SaveDefaults(tr, db, axisName, left, right);
                edgeCount = LaneEdgeDrawingService.Redraw(tr, db, axis, definitions);
                ProfileViewRefresh.RefreshIfExists(tr, db, axisName);
                tr.Commit();
            }

            ed.WriteMessage(
                $"\nTCM-ROADS: Tip '{evaluated.TemplateName}' za '{axisName}' — " +
                $"kolovoz L={left:0.###} / D={right:0.###} m; " +
                $"nacrtano {edgeCount} elemenata (ivice/granice/hatch).");
            ed.Regen();
            ProfileViewRefresh.ScheduleIfExists(doc, axisName);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static bool TryChooseAxis(
        Editor ed,
        Database db,
        out string axisName,
        out RoadAxis axis)
    {
        axisName = string.Empty;
        axis = null!;

        IReadOnlyList<string> names;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            names = RoadAxisStore.GetAxisNames(tr, db);
            tr.Commit();
        }

        if (names.Count == 0)
        {
            ed.WriteMessage(
                "\nTCM-ROADS: U crtežu nema definisanih osovina. " +
                "Prvo kreirajte osovinu komandom TCMPLO2TAN.");
            return false;
        }

        var chooser = new AxisSelectionDialog(names);
        AcApp.ShowModalWindow(chooser);
        if (chooser.CloseAction == AxisSelectionCloseAction.PickInDrawing)
        {
            return TryPickAxis(ed, db, out axisName, out axis);
        }

        if (chooser.CloseAction != AxisSelectionCloseAction.Selected)
        {
            return false;
        }

        axisName = chooser.SelectedAxisName;
        using var loadTr = db.TransactionManager.StartTransaction();
        var metadata = RoadAxisStore.Load(loadTr, db, axisName);
        var loaded = metadata is null
            ? null
            : AxisGeometryReader.ReadAxis(loadTr, db, axisName, metadata.StartStation);
        loadTr.Commit();
        if (loaded is null || loaded.Elements.Count == 0)
        {
            ed.WriteMessage(
                $"\nTCM-ROADS: Geometrija osovine '{axisName}' nije dostupna.");
            axisName = string.Empty;
            return false;
        }

        axis = loaded;
        return true;
    }

    private static double? Interpolate(IReadOnlyList<(double Station, double Elevation)> samples, double station)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        if (station <= samples[0].Station)
        {
            return samples[0].Elevation;
        }

        if (station >= samples[^1].Station)
        {
            return samples[^1].Elevation;
        }

        for (var i = 0; i < samples.Count - 1; i++)
        {
            var a = samples[i];
            var b = samples[i + 1];
            if (station < a.Station || station > b.Station)
            {
                continue;
            }

            var span = b.Station - a.Station;
            if (Math.Abs(span) < 1e-9)
            {
                return a.Elevation;
            }

            var t = (station - a.Station) / span;
            return a.Elevation + t * (b.Elevation - a.Elevation);
        }

        return null;
    }
}
