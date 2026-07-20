using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads.BestFit;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    [CommandMethod("TCMBESTFIT", CommandFlags.Modal)]
    public void CreateBestFitAxis()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        try
        {
            var db = doc.Database;
            var sourceId = SelectPolyline(ed, db);
            if (sourceId.IsNull)
            {
                return;
            }

            var deviationPrompt = new PromptDoubleOptions(
                "\nMaksimalno odstupanje Best Fit pravca <0.50 m>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 0.50,
                UseDefaultValue = true
            };
            var deviationResult = ed.GetDouble(deviationPrompt);
            if (deviationResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            var maxDeviation = deviationResult.Status == PromptStatus.OK
                ? deviationResult.Value
                : 0.50;

            IReadOnlyList<Autodesk.AutoCAD.Geometry.Point2d> fittedVertices;
            IReadOnlyList<string> existingAxisNames;
            string suggestedName;
            using (var readTr = db.TransactionManager.StartTransaction())
            {
                var source = (Polyline)readTr.GetObject(sourceId, OpenMode.ForRead);
                fittedVertices = AxisBestFit.FitFromPolyline(
                    source,
                    new AxisBestFitOptions { MaxDeviation = maxDeviation });
                existingAxisNames = RoadAxisStore.GetAxisNames(readTr, db);
                suggestedName = RoadAxisStore.GetNextAvailableName(readTr, db);
                readTr.Commit();
            }

            if (fittedVertices.Count < 2)
            {
                ed.WriteMessage("\nTCM-ROADS: Best Fit nije pronašao upotrebljivu osovinu.");
                return;
            }

            var fittedLength = 0.0;
            for (var index = 1; index < fittedVertices.Count; index++)
            {
                fittedLength += fittedVertices[index - 1].GetDistanceTo(fittedVertices[index]);
            }

            var dialogState = new Plo2TanDialogState
            {
                AxisName = suggestedName,
                EndStation = fittedLength
            };
            Plo2TanDialogPreferences.ApplyTo(dialogState);
            dialogState.AxisName = suggestedName;
            dialogState.EndStation = fittedLength;

            var dialog = new Plo2TanDialog(fittedLength, dialogState, existingAxisNames);
            if (AcApp.ShowModalWindow(dialog) != true ||
                dialog.CloseAction != Plo2TanDialogCloseAction.Confirmed)
            {
                ed.WriteMessage("\nTCM-ROADS: Best Fit komanda otkazana.");
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            if (RoadAxisStore.Exists(tr, db, dialog.AxisName))
            {
                ed.WriteMessage($"\nTCM-ROADS: Osovina '{dialog.AxisName}' već postoji.");
                return;
            }

            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);
            var fittedPolyline = new Polyline(fittedVertices.Count);
            for (var index = 0; index < fittedVertices.Count; index++)
            {
                fittedPolyline.AddVertexAt(index, fittedVertices[index], 0, 0, 0);
            }

            modelSpace.AppendEntity(fittedPolyline);
            tr.AddNewlyCreatedDBObject(fittedPolyline, true);

            var fullAxis = PolylineToTangentConverter.Convert(
                fittedVertices, dialog.CurveRadius, 0, dialog.AxisName);
            var stationOptions = AxisStationMapper.MapLabelOptionsToAxis(
                fittedPolyline, fullAxis, dialog.StationOptions);
            var visibleAxis = RoadAxisTrimmer.Trim(
                fullAxis, stationOptions.StartStation, stationOptions.EndStation);

            DrawAxis(tr, modelSpace, visibleAxis, fittedPolyline.ObjectId, stationOptions.AxisColorIndex);
            var labelCount = RoadDrawing.DrawStationLabels(
                tr, modelSpace, visibleAxis, stationOptions);
            var radiusCount = RoadDrawing.DrawRadiusLabels(
                tr, modelSpace, visibleAxis, dialog.TextHeight, stationOptions.LabelSideSign);
            var segmentCount = stationOptions.DrawSegmentLabels
                ? RoadDrawing.DrawSegmentLabels(
                    tr, modelSpace, visibleAxis, dialog.TextHeight,
                    stationOptions.LabelSideSign, stationOptions.SegmentLabelColorIndex)
                : 0;
            var nodeCount = RoadDrawing.DrawTangentNodeTables(
                tr, modelSpace, visibleAxis, dialog.TextHeight);

            if (visibleAxis.Elements.Count > 0)
            {
                AxisReferenceTracker.Update(dialog.AxisName, visibleAxis.Elements[0].Start);
            }

            RoadXData.AttachSourcePolyline(fittedPolyline, dialog.AxisName);
            RoadDrawing.StyleSourcePolyline(tr, db, fittedPolyline);
            RoadDrawing.SaveAxisMetadata(
                tr, db, fullAxis, stationOptions, dialog.CurveRadius,
                fittedPolyline.ObjectId, dialog.StartStation, dialog.EndStation);
            tr.Commit();

            ed.WriteMessage(
                $"\nTCM-ROADS: Best Fit je sveo {GetSourceVertexCount(db, sourceId)} tačaka " +
                $"na {fittedVertices.Count} PI temena (odstupanje {maxDeviation:F2} m).");
            PrintAxisReport(ed, visibleAxis, labelCount, radiusCount, segmentCount, nodeCount);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS Best Fit greška: {ex.Message}");
        }
    }

    private static int GetSourceVertexCount(Database db, ObjectId sourceId)
    {
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        return tr.GetObject(sourceId, OpenMode.ForRead) is Polyline source
            ? source.NumberOfVertices
            : 0;
    }
}
