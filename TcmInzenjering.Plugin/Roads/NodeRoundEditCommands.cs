using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    /// <summary>
    /// Plateia 21F2 stil — UredjenjeKrivine na TS čvoru (R, L1/L2, r_L/r_R).
    /// Korisnik bira tangentu/osovinu; uređuje se čvor najbliži mestu klika.
    /// </summary>
    [CommandMethod("TCMZAOUREDI", CommandFlags.Modal)]
    public void ManualNodeRoundEdit()
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
            if (!TryChooseAxisForNodeEdit(ed, db, out var axisName, out var nodeNumber))
            {
                return;
            }

            RoadAxisMetadata? metadata;
            IReadOnlyList<TangentNodeInfo> nodes;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                metadata = RoadAxisStore.Load(tr, db, axisName);
                if (metadata is null)
                {
                    ed.WriteMessage($"\nTCM-ROADS: Nema metapodataka za osovinu „{axisName}“.");
                    tr.Commit();
                    return;
                }

                var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
                if (axis is null)
                {
                    ed.WriteMessage("\nTCM-ROADS: Osovina nije pronadjena u crtezu.");
                    tr.Commit();
                    return;
                }

                nodes = TangentNodeGeometry.Collect(axis);
                tr.Commit();
            }

            if (nodes.Count == 0)
            {
                ed.WriteMessage("\nTCM-ROADS: Nema krivina (TS cvorova) na osovini.");
                return;
            }

            var initialIndex = Math.Max(0, nodes.ToList().FindIndex(n => n.Number == nodeNumber));
            ed.WriteMessage($"\nTCM-ROADS: Uredjuje se TS{nodes[initialIndex].Number} (najblizi kliku).");

#if BRICSCAD
            var node = nodes[initialIndex];
            var radiusPrompt = PromptDouble(
                ed,
                $"\nRadijus R za TS{node.Number} <{node.Radius:0.###}>: ",
                node.Radius);
            if (radiusPrompt is null)
            {
                return;
            }

            ApplyCornerCurve(ed, db, axisName, node, radiusPrompt.Value, 0, 0, metadata!.StartStation);
#else
            var state = new NodeRoundEditState { NodeIndex = initialIndex };
            // Popuni L1/L2 iz postojećeg čvora ako već ima prelaznice.
            if (nodes[initialIndex].L1 > 1e-6 || nodes[initialIndex].L2 > 1e-6)
            {
                state.R = nodes[initialIndex].Radius;
                state.L1 = nodes[initialIndex].L1;
                state.L2 = nodes[initialIndex].L2;
                state.IsManual = true;
            }
            while (true)
            {
                var dialog = new NodeRoundEditDialog(nodes, state, metadata!.CurveRadius);
                AcApp.ShowModalWindow(dialog);

                if (dialog.CloseAction == NodeRoundCloseAction.Applied)
                {
                    ApplyPendingCornerEdits(
                        ed,
                        db,
                        axisName,
                        nodes,
                        dialog.PendingEdits,
                        metadata.StartStation);
                    break;
                }

                if (dialog.CloseAction == NodeRoundCloseAction.Cancel)
                {
                    break;
                }

                if (dialog.CloseAction == NodeRoundCloseAction.PickRadius)
                {
                    if (TryPickDistanceFromDrawing(ed, dialog.CurrentPi, "R", out var r))
                    {
                        state.R = r;
                        state.IsManual = true;
                        UpsertActiveDraft(state, nodes, r: r, isManual: true);
                    }

                    continue;
                }

                if (dialog.CloseAction == NodeRoundCloseAction.PickTangentLeft)
                {
                    if (TryPickDistanceFromDrawing(ed, dialog.CurrentPi, "r_L", out var rl))
                    {
                        state.Rl = rl;
                        state.IsManual = true;
                        var node = nodes[MathNet48.Clamp(state.NodeIndex, 0, nodes.Count - 1)];
                        var tanHalf = Math.Tan(node.DeflectionRadians / 2.0);
                        if (tanHalf > 1e-12)
                        {
                            state.R = rl / tanHalf;
                        }

                        UpsertActiveDraft(state, nodes, r: state.R, rl: rl, isManual: true);
                    }

                    continue;
                }

                if (dialog.CloseAction == NodeRoundCloseAction.PickTangentRight)
                {
                    if (TryPickDistanceFromDrawing(ed, dialog.CurrentPi, "r_R", out var rr))
                    {
                        state.Rr = rr;
                        state.IsManual = true;
                        var node = nodes[MathNet48.Clamp(state.NodeIndex, 0, nodes.Count - 1)];
                        var tanHalf = Math.Tan(node.DeflectionRadians / 2.0);
                        if (tanHalf > 1e-12)
                        {
                            state.R = rr / tanHalf;
                        }

                        UpsertActiveDraft(state, nodes, r: state.R, rr: rr, isManual: true);
                    }
                }
            }
#endif
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
            if (ex.InnerException is not null)
            {
                ed.WriteMessage($" ({ex.InnerException.Message})");
            }

            var line = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                ed.WriteMessage($"\n  {line}");
            }
        }
    }

    private static void UpsertActiveDraft(
        NodeRoundEditState state,
        IReadOnlyList<TangentNodeInfo> nodes,
        double? r = null,
        double? rl = null,
        double? rr = null,
        bool? isManual = null)
    {
        var index = MathNet48.Clamp(state.NodeIndex, 0, nodes.Count - 1);
        if (!state.NodeDrafts.TryGetValue(index, out var draft))
        {
            draft = new NodeRoundNodeDraft
            {
                NodeIndex = index,
                NodeNumber = nodes[index].Number,
                IsManual = state.IsManual,
                ManualMode = state.ManualMode,
                R = state.R,
                L1 = state.L1,
                L2 = state.L2,
                R1 = state.R1,
                R2 = state.R2,
                Rl = state.Rl,
                Rr = state.Rr,
                RaRatio = state.RaRatio,
                Prelaznice = state.Prelaznice
            };
            state.NodeDrafts[index] = draft;
        }

        if (r is double radius)
        {
            draft.R = radius;
            state.R = radius;
        }

        if (rl is double left)
        {
            draft.Rl = left;
            state.Rl = left;
        }

        if (rr is double right)
        {
            draft.Rr = right;
            state.Rr = right;
        }

        if (isManual is bool manual)
        {
            draft.IsManual = manual;
            state.IsManual = manual;
        }
    }

    private static void ApplyPendingCornerEdits(
        Editor ed,
        Database db,
        string axisName,
        IReadOnlyList<TangentNodeInfo> nodes,
        IReadOnlyList<NodeRoundNodeDraft> drafts,
        double startStation)
    {
        var ordered = drafts
            .Where(draft => draft.R > 1e-6)
            .OrderBy(draft => draft.NodeIndex)
            .ToList();
        if (ordered.Count == 0)
        {
            if (nodes.Count == 0)
            {
                return;
            }

            ApplyCornerCurve(ed, db, axisName, nodes[0], nodes[0].Radius, 0, 0, startStation);
            return;
        }

        var applied = 0;
        foreach (var draft in ordered)
        {
            TangentNodeInfo? node = null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
                if (axis is not null)
                {
                    var fresh = TangentNodeGeometry.Collect(axis);
                    node = fresh.FirstOrDefault(item => item.Number == draft.NodeNumber);
                    if (node is null &&
                        draft.NodeIndex >= 0 &&
                        draft.NodeIndex < fresh.Count)
                    {
                        node = fresh[draft.NodeIndex];
                    }
                }

                tr.Commit();
            }

            if (node is null)
            {
                ed.WriteMessage(
                    $"\nTCM-ROADS: TS{draft.NodeNumber} nije pronadjen nakon prethodnih izmena — preskacem.");
                continue;
            }

            ApplyCornerCurve(
                ed,
                db,
                axisName,
                node,
                draft.R,
                draft.L1,
                draft.L2,
                startStation);
            applied++;
        }

        if (applied > 1)
        {
            ed.WriteMessage($"\nTCM-ROADS: Primenjeno {applied} TS zaobljenja u jednom OK.");
        }
    }

    private static bool TryPickDistanceFromDrawing(
        Editor ed,
        Point3d basePoint,
        string label,
        out double distance)
    {
        distance = 0;
        var opts = new PromptDistanceOptions(
            $"\nOdredite {label} u crtezu (Enter = otkaz): ")
        {
            AllowNone = true,
            AllowZero = false,
            AllowNegative = false,
            UseBasePoint = true,
            BasePoint = basePoint,
            UseDashedLine = true
        };

        var result = ed.GetDistance(opts);
        if (result.Status != PromptStatus.OK)
        {
            ed.WriteMessage($"\nTCM-ROADS: Unos {label} otkazan.");
            return false;
        }

        distance = Math.Abs(result.Value);
        if (distance <= 1e-9)
        {
            ed.WriteMessage($"\nTCM-ROADS: {label} mora biti > 0.");
            return false;
        }

        ed.WriteMessage($"\nTCM-ROADS: {label} = {distance:0.###}");
        return true;
    }

    private static void ApplyCornerCurve(
        Editor ed,
        Database db,
        string axisName,
        TangentNodeInfo node,
        double radius,
        double l1,
        double l2,
        double startStation)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var applied = false;
        string? error = null;
        RoadDrawing.RunWithUnlockedAxisLayer(tr, db, () =>
        {
            applied = CornerCurveApplicator.Apply(
                tr,
                db,
                axisName,
                node.Number,
                radius,
                l1,
                l2,
                startStation,
                out error);
            if (!applied)
            {
                return;
            }

            var updated = StationLabelService.RefreshAfterCornerEdit(tr, db, axisName);
            var spiralMsg = l1 > 1e-6 || l2 > 1e-6
                ? $", L1={l1:0.###}, L2={l2:0.###}"
                : string.Empty;
            ed.WriteMessage(
                $"\nTCM-ROADS: TS{node.Number} — R={radius:0.###}{spiralMsg} primenjeno " +
                $"(osovina, stacionaze, projekcija/poduzni: {updated}).");
        });

        if (!applied)
        {
            var msg = error ?? "Zaobljenje nije moguce sa zadatim parametrima.";
            ed.WriteMessage($"\nTCM-ROADS: {msg}");
#if !BRICSCAD
            System.Windows.MessageBox.Show(
                msg,
                "TCM-ROADS — UredjenjeKrivine",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
#endif
            tr.Abort();
            return;
        }

        tr.Commit();
    }

    /// <summary>
    /// Prvo nudi listu osa; opcija „Izaberi u crtežu“ zadržava izbor
    /// najbližeg TS čvora klikom.
    /// </summary>
    private static bool TryChooseAxisForNodeEdit(
        Editor ed,
        Database db,
        out string axisName,
        out int nodeNumber)
    {
        axisName = string.Empty;
        nodeNumber = 0;

        IReadOnlyList<string> names;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            names = RoadAxisStore.GetAxisNames(tr, db);
            tr.Commit();
        }

        if (names.Count == 0)
        {
            ed.WriteMessage("\nTCM-ROADS: U crtežu nema definisanih osovina.");
            return false;
        }

        var chooser = new AxisSelectionDialog(names);
        AcApp.ShowModalWindow(chooser);
        if (chooser.CloseAction == AxisSelectionCloseAction.PickInDrawing)
        {
            return TryPickAxisNearNode(ed, db, out axisName, out nodeNumber);
        }

        if (chooser.CloseAction != AxisSelectionCloseAction.Selected)
        {
            return false;
        }

        axisName = chooser.SelectedAxisName;
        using var loadTr = db.TransactionManager.StartTransaction();
        var metadata = RoadAxisStore.Load(loadTr, db, axisName);
        var axis = metadata is null
            ? null
            : AxisGeometryReader.ReadAxis(loadTr, db, axisName, metadata.StartStation);
        var nodes = axis is null
            ? Array.Empty<TangentNodeInfo>()
            : TangentNodeGeometry.Collect(axis).ToArray();
        loadTr.Commit();
        if (nodes.Length == 0)
        {
            ed.WriteMessage($"\nTCM-ROADS: Osovina „{axisName}“ nema TS čvorove.");
            axisName = string.Empty;
            return false;
        }

        nodeNumber = nodes[0].Number;
        return true;
    }

    /// <summary>
    /// Bira tangentu / luk / izvornu polylinu osovine; čvor = najbliži PI mestu klika.
    /// </summary>
    private static bool TryPickAxisNearNode(
        Editor ed,
        Database db,
        out string axisName,
        out int nodeNumber)
    {
        axisName = "";
        nodeNumber = 0;

        var peo = new PromptEntityOptions(
            "\nIzaberite tangentu / osovinu (klik blizu zeljenog zaobljenja): ")
        {
            AllowNone = false
        };
        peo.SetRejectMessage("\nIzaberite Line, Arc ili Polyline osovine.");
        peo.AddAllowedClass(typeof(Line), exactMatch: false);
        peo.AddAllowedClass(typeof(Arc), exactMatch: false);
        peo.AddAllowedClass(typeof(Polyline), exactMatch: false);

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

        if (!TryResolveAxisName(entity, out axisName))
        {
            tr.Commit();
            ed.WriteMessage("\nTCM-ROADS: Izabrani entitet nije TCM osovina / tangenta.");
            return false;
        }

        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            tr.Commit();
            ed.WriteMessage($"\nTCM-ROADS: Nema metapodataka za osovinu „{axisName}“.");
            return false;
        }

        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        if (axis is null)
        {
            tr.Commit();
            ed.WriteMessage("\nTCM-ROADS: Osovina nije pronadjena u crtezu.");
            return false;
        }

        var nodes = TangentNodeGeometry.Collect(axis);
        if (nodes.Count == 0)
        {
            tr.Commit();
            ed.WriteMessage("\nTCM-ROADS: Nema krivina (TS cvorova) na osovini.");
            return false;
        }

        var pick = per.PickedPoint;
        nodeNumber = FindNearestNodeNumber(nodes, pick);
        tr.Commit();
        return true;
    }

    private static bool TryResolveAxisName(Entity entity, out string axisName)
    {
        axisName = "";

        if (RoadXData.TryReadAxisElement(entity, out axisName, out _))
        {
            return true;
        }

        if (RoadXData.TryReadSourcePolyline(entity, out axisName))
        {
            return true;
        }

        if (RoadXData.TryReadTangentNode(entity, out axisName, out _))
        {
            return true;
        }

        return false;
    }

    private static int FindNearestNodeNumber(IReadOnlyList<TangentNodeInfo> nodes, Point3d pick)
    {
        var best = nodes[0];
        var bestDist = XyDistance(pick, best.Pi);
        for (var i = 1; i < nodes.Count; i++)
        {
            var d = XyDistance(pick, nodes[i].Pi);
            if (d < bestDist)
            {
                bestDist = d;
                best = nodes[i];
            }
        }

        return best.Number;
    }
}
