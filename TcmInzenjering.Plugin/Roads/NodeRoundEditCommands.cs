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
            if (!TryPickAxisNearNode(ed, db, out var axisName, out var nodeNumber))
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
                    ed.WriteMessage($"\nTCM-INZINJERING: Nema metapodataka za osovinu „{axisName}“.");
                    tr.Commit();
                    return;
                }

                var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
                if (axis is null)
                {
                    ed.WriteMessage("\nTCM-INZINJERING: Osovina nije pronadjena u crtezu.");
                    tr.Commit();
                    return;
                }

                nodes = TangentNodeGeometry.Collect(axis);
                tr.Commit();
            }

            if (nodes.Count == 0)
            {
                ed.WriteMessage("\nTCM-INZINJERING: Nema krivina (TS cvorova) na osovini.");
                return;
            }

            var initialIndex = Math.Max(0, nodes.ToList().FindIndex(n => n.Number == nodeNumber));
            ed.WriteMessage($"\nTCM-INZINJERING: Uredjuje se TS{nodes[initialIndex].Number} (najblizi kliku).");

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
                    var selected = nodes[MathNet48.Clamp(state.NodeIndex, 0, nodes.Count - 1)];
                    ApplyCornerCurve(
                        ed,
                        db,
                        axisName,
                        selected,
                        state.R,
                        state.L1,
                        state.L2,
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
                    }
                }
            }
#endif
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
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
            ed.WriteMessage($"\nTCM-INZINJERING: Unos {label} otkazan.");
            return false;
        }

        distance = Math.Abs(result.Value);
        if (distance <= 1e-9)
        {
            ed.WriteMessage($"\nTCM-INZINJERING: {label} mora biti > 0.");
            return false;
        }

        ed.WriteMessage($"\nTCM-INZINJERING: {label} = {distance:0.###}");
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
                $"\nTCM-INZINJERING: TS{node.Number} — R={radius:0.###}{spiralMsg} primenjeno " +
                $"(osovina, stacionaze, projekcija/poduzni: {updated}).");
        });

        if (!applied)
        {
            var msg = error ?? "Zaobljenje nije moguce sa zadatim parametrima.";
            ed.WriteMessage($"\nTCM-INZINJERING: {msg}");
#if !BRICSCAD
            System.Windows.MessageBox.Show(
                msg,
                "TCM-INŽINJERING — UredjenjeKrivine",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
#endif
            tr.Abort();
            return;
        }

        tr.Commit();
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
            ed.WriteMessage("\nTCM-INZINJERING: Izabrani entitet nije TCM osovina / tangenta.");
            return false;
        }

        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            tr.Commit();
            ed.WriteMessage($"\nTCM-INZINJERING: Nema metapodataka za osovinu „{axisName}“.");
            return false;
        }

        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        if (axis is null)
        {
            tr.Commit();
            ed.WriteMessage("\nTCM-INZINJERING: Osovina nije pronadjena u crtezu.");
            return false;
        }

        var nodes = TangentNodeGeometry.Collect(axis);
        if (nodes.Count == 0)
        {
            tr.Commit();
            ed.WriteMessage("\nTCM-INZINJERING: Nema krivina (TS cvorova) na osovini.");
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
