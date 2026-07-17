using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    [CommandMethod("TCMPLO2TAN", CommandFlags.Modal)]
    public void PolylineToTangentPolygon()
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
            var polylineId = SelectPolyline(ed, db);
            if (polylineId == ObjectId.Null)
            {
                return;
            }

            double polylineLength;
            using (var previewTr = db.TransactionManager.StartTransaction())
            {
                var preview = (Polyline)previewTr.GetObject(polylineId, OpenMode.ForRead);
                polylineLength = preview.Length;
                previewTr.Commit();
            }

#if BRICSCAD
            if (!LegacyPlo2TanPrompt.TryCollect(ed, polylineLength, out var axisName, out var curveRadius, out var textHeight, out var stationOptions))
            {
                ed.WriteMessage("\nTCM-INZINJERING: komanda otkazana.");
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
            var axis = PolylineToTangentConverter.Convert(polyline, curveRadius, stationOptions.StartStation, axisName);
            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            DrawAxis(tr, modelSpace, axis, polylineId, stationOptions.AxisColorIndex);
            var labelCount = RoadDrawing.DrawStationLabels(tr, modelSpace, axis, stationOptions);
            var radiusCount = 0;
            var segmentCount = stationOptions.DrawSegmentLabels
                ? RoadDrawing.DrawSegmentLabels(
                    tr,
                    modelSpace,
                    axis,
                    textHeight,
                    stationOptions.LabelSideSign,
                    stationOptions.SegmentLabelColorIndex)
                : 0;
            var nodeCount = RoadDrawing.DrawTangentNodeTables(tr, modelSpace, axis, textHeight);
            if (axis.Elements.Count > 0)
            {
                AxisReferenceTracker.Update(axis.Name, axis.Elements[0].Start);
            }

            var polylineForLink = (Polyline)tr.GetObject(polylineId, OpenMode.ForWrite);
            RoadXData.AttachSourcePolyline(polylineForLink, axisName);
            RoadDrawing.StyleSourcePolyline(tr, db, polylineForLink);
            RoadDrawing.SaveAxisMetadata(tr, db, axis, stationOptions, curveRadius, polylineId);
            tr.Commit();

            PrintAxisReport(ed, axis, labelCount, radiusCount, segmentCount, nodeCount);
#else
            var dialogState = new Plo2TanDialogState
            {
                EndStation = polylineLength
            };
            Plo2TanDialogPreferences.ApplyTo(dialogState);

            Plo2TanDialog? dialog = null;
            while (true)
            {
                dialog = new Plo2TanDialog(polylineLength, dialogState);
                var accepted = AcApp.ShowModalWindow(dialog) == true;
                if (accepted && dialog.CloseAction == Plo2TanDialogCloseAction.Confirmed)
                {
                    break;
                }

                switch (dialog.CloseAction)
                {
                    case Plo2TanDialogCloseAction.PickStartStation:
                        if (PolylineStationPicker.TryPickDistance(
                                doc,
                                polylineId,
                                "Odredite tacku na polyliniji za pocetak stacionaza:",
                                out var startDistance))
                        {
                            dialogState.StartStation = Math.Max(0, Math.Min(startDistance, polylineLength));
                            if (dialogState.EndStation < dialogState.StartStation)
                            {
                                dialogState.EndStation = polylineLength;
                            }
                        }

                        continue;
                    case Plo2TanDialogCloseAction.PickEndStation:
                        if (PolylineStationPicker.TryPickDistance(
                                doc,
                                polylineId,
                                "Odredite tacku na polyliniji za kraj stacionaza:",
                                out var endDistance))
                        {
                            var minEnd = Math.Max(0, dialogState.StartStation);
                            dialogState.EndStation = Math.Max(minEnd, Math.Min(endDistance, polylineLength));
                        }

                        continue;
                    default:
                        ed.WriteMessage("\nTCM-INZINJERING: komanda otkazana.");
                        return;
                }
            }

            using var tr = db.TransactionManager.StartTransaction();
            var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
            var fullAxis = PolylineToTangentConverter.Convert(
                polyline,
                dialog!.CurveRadius,
                0,
                dialog.AxisName);
            var stationOptions = AxisStationMapper.MapLabelOptionsToAxis(
                polyline,
                fullAxis,
                dialog.StationOptions);
            var visibleAxis = RoadAxisTrimmer.Trim(
                fullAxis,
                stationOptions.StartStation,
                stationOptions.EndStation);
            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            // Ponovni OK (uredjivanje polozaja / tipa oznake) — obrisi staro, pa iscrtaj iznova.
            RoadDrawing.RunWithUnlockedAxisLayer(tr, db, () =>
            {
                StationLabelService.DeleteLabels(tr, db, dialog.AxisName);
                StationLabelService.DeleteCrossAnnotations(tr, db, dialog.AxisName);
                StationLabelService.DeleteSegmentLabels(tr, db, dialog.AxisName);
                StationLabelService.DeleteRadiusLabels(tr, db, dialog.AxisName);
                StationLabelService.DeleteTangentNodeTables(tr, db, dialog.AxisName);
                StationLabelService.DeleteAxisEntities(tr, db, dialog.AxisName);
            });

            DrawAxis(tr, modelSpace, visibleAxis, polylineId, stationOptions.AxisColorIndex);
            var labelCount = RoadDrawing.DrawStationLabels(tr, modelSpace, visibleAxis, stationOptions);
            var radiusCount = 0;
            var segmentCount = stationOptions.DrawSegmentLabels
                ? RoadDrawing.DrawSegmentLabels(
                    tr,
                    modelSpace,
                    visibleAxis,
                    dialog.TextHeight,
                    stationOptions.LabelSideSign,
                    stationOptions.SegmentLabelColorIndex)
                : 0;
            var nodeCount = RoadDrawing.DrawTangentNodeTables(tr, modelSpace, visibleAxis, dialog.TextHeight);
            if (visibleAxis.Elements.Count > 0)
            {
                AxisReferenceTracker.Update(dialog.AxisName, visibleAxis.Elements[0].Start);
            }

            var polylineForLink = (Polyline)tr.GetObject(polylineId, OpenMode.ForWrite);
            RoadXData.AttachSourcePolyline(polylineForLink, dialog.AxisName);
            RoadDrawing.StyleSourcePolyline(tr, db, polylineForLink);
            RoadDrawing.SaveAxisMetadata(
                tr,
                db,
                fullAxis,
                stationOptions,
                dialog.CurveRadius,
                polylineId,
                dialog.StartStation,
                dialog.EndStation);
            tr.Commit();

            ed.WriteMessage(
                $"\n  Interval crtanja osovine: {stationOptions.StartStation:F2} m - {stationOptions.EndStation:F2} m (odabrano na polyliniji: {dialog.StartStation:F2} - {dialog.EndStation:F2} m)");
            PrintAxisReport(ed, visibleAxis, labelCount, radiusCount, segmentCount, nodeCount);
#endif
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {acEx.Message} ({acEx.ErrorStatus})");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
            if (ex.InnerException is not null)
            {
                ed.WriteMessage($" ({ex.InnerException.Message})");
            }

            if (ex.StackTrace is not null)
            {
                ed.WriteMessage($"\n  {ex.StackTrace.Split('\n')[0].Trim()}");
            }
        }
    }

    [CommandMethod("TCMSTACOZN", CommandFlags.Modal)]
    public void DrawStationLabelsCommand()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var db = doc.Database;
        var ed = doc.Editor;

        var polylineId = SelectPolyline(ed, db);
        if (polylineId == ObjectId.Null)
        {
            return;
        }

        var radius = PromptDouble(ed, "\nRadijus krivina [m] <50>: ", 50);
        if (radius is null)
        {
            return;
        }

        var startStation = PromptDouble(ed, "\nPocetna stacionaza [m] <0>: ", 0);
        if (startStation is null)
        {
            return;
        }

        var interval = PromptDouble(ed, "\nRazmak oznaka stacionaze [m] <20>: ", 20);
        if (interval is null)
        {
            return;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
        var axis = PolylineToTangentConverter.Convert(polyline, radius.Value, startStation.Value, "OS-1");
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var labelCount = RoadDrawing.DrawStationLabels(tr, modelSpace, axis, interval.Value, RoadDrawing.DefaultTickLength, 2.5, "STA ");
        tr.Commit();

        ed.WriteMessage($"\nTCM-INZINJERING: Iscrtano {labelCount} oznaka stacionaze.");
    }

    [CommandMethod("TCMSTACAZUR", CommandFlags.Modal)]
    public void RefreshStationLabels()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        var db = doc.Database;

        var axisName = PromptString(ed, "\nIme osovine za azuriranje stacionaza <OS-1>: ", "OS-1");
        if (axisName is null)
        {
            return;
        }

        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var count = StationLabelService.RefreshAxis(tr, db, axisName);
            tr.Commit();
            ed.WriteMessage($"\nTCM-INZINJERING: Azurirana geometrija i {count} oznaka stacionaze za '{axisName}'.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
            if (ex.InnerException is not null)
            {
                ed.WriteMessage($" ({ex.InnerException.Message})");
            }
        }
    }

    [CommandMethod("TCMOSINFO", CommandFlags.Modal)]
    public void AxisInfo()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var db = doc.Database;
        var ed = doc.Editor;

        var polylineId = SelectPolyline(ed, db);
        if (polylineId == ObjectId.Null)
        {
            return;
        }

        var radius = PromptDouble(ed, "\nRadijus krivina [m] <50>: ", 50);
        if (radius is null)
        {
            return;
        }

        var startStation = PromptDouble(ed, "\nPocetna stacionaza [m] <0>: ", 0);
        if (startStation is null)
        {
            return;
        }

        using var tr = doc.Database.TransactionManager.StartTransaction();
        var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
        var axis = PolylineToTangentConverter.Convert(polyline, radius.Value, startStation.Value, "OS-1");
        tr.Commit();

        PrintAxisReport(ed, axis, 0);
    }

    [CommandMethod("TCMPOPOSPOZ", CommandFlags.Modal)]
    public void ConfigureCrossAxisPlacement()
    {
        CrossAxisCommandService.Run(AcApp.DocumentManager.MdiActiveDocument);
    }

#if !BRICSCAD
    [CommandMethod("TCMPOPSTAC", CommandFlags.Modal)]
    public void DrawCrossAxisAtStation()
    {
        CrossAxisDrawCommandService.Run(AcApp.DocumentManager.MdiActiveDocument);
    }

    [CommandMethod("TCMPOPBRISI", CommandFlags.Modal)]
    public void DeleteCrossAxes()
    {
        CrossAxisDeleteCommandService.Run(AcApp.DocumentManager.MdiActiveDocument);
    }
#endif

    [CommandMethod("TCMOSTAB", CommandFlags.Modal)]
    public void InsertAxisTable()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        var axisName = PromptString(ed, "\nIme osovine za tabelu <OS-1>: ", "OS-1");
        if (axisName is null)
        {
            return;
        }

        var pointResult = ed.GetPoint(new PromptPointOptions("\nOdredite polozaj tabele osovine: "));
        if (pointResult.Status != PromptStatus.OK)
        {
            return;
        }

        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var metadata = RoadAxisStore.Load(tr, db, axisName);
            if (metadata is null)
            {
                ed.WriteMessage($"\nTCM-INZINJERING: osovina '{axisName}' nije pronadjena.");
                return;
            }

            var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
            if (axis is null)
            {
                ed.WriteMessage($"\nTCM-INZINJERING: nema nacrtane osovine '{axisName}'.");
                return;
            }

            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);
            var count = RoadDrawing.DrawAxisTable(
                tr,
                modelSpace,
                axis,
                pointResult.Value,
                metadata.TextHeight);
            tr.Commit();
            ed.WriteMessage($"\nTCM-INZINJERING: Ubacena tabela osovine '{axisName}' ({count} elemenata).");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
        }
    }

    private static ObjectId SelectPolyline(Editor ed, Database db)
    {
        try
        {
            var options = new PromptEntityOptions("\nIzaberite polylinu osovine: ")
            {
                AllowNone = false
            };
            options.SetRejectMessage("\nIzaberite LWPOLYLINE ili 2D POLYLINE (ne 3D polyline / Alignment).");
            options.AddAllowedClass(typeof(Polyline), exactMatch: false);
            options.AddAllowedClass(typeof(Polyline2d), exactMatch: false);
            var result = ed.GetEntity(options);
            if (result.Status != PromptStatus.OK || result.ObjectId.IsNull || result.ObjectId.IsErased)
            {
                return ObjectId.Null;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var entity = tr.GetObject(result.ObjectId, OpenMode.ForRead, openErased: false, forceOpenOnLockedLayer: true);
            if (entity is Polyline pl)
            {
                if (pl.NumberOfVertices < 2)
                {
                    ed.WriteMessage("\nTCM-INZINJERING: polilinija mora imati bar 2 temena.");
                    tr.Commit();
                    return ObjectId.Null;
                }

                tr.Commit();
                return result.ObjectId;
            }

            if (entity is Polyline2d poly2d)
            {
                var convertedId = ConvertPolyline2dToLightweight(tr, db, poly2d);
                tr.Commit();
                if (convertedId.IsNull)
                {
                    ed.WriteMessage("\nTCM-INZINJERING: nije uspela konverzija POLYLINE u LWPOLYLINE.");
                }
                else
                {
                    ed.WriteMessage("\nTCM-INZINJERING: stara POLYLINE je konvertovana u LWPOLYLINE.");
                }

                return convertedId;
            }

            tr.Commit();
            return ObjectId.Null;
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING: izbor polilinije nije uspeo — {ex.Message}");
            return ObjectId.Null;
        }
    }

    private static ObjectId ConvertPolyline2dToLightweight(Transaction tr, Database db, Polyline2d source)
    {
        source.UpgradeOpen();
        var lw = new Polyline();
        var index = 0;
        foreach (ObjectId vertexId in source)
        {
            if (tr.GetObject(vertexId, OpenMode.ForRead) is not Vertex2d vertex)
            {
                continue;
            }

            if (vertex.VertexType == Vertex2dType.SplineControlVertex)
            {
                continue;
            }

            lw.AddVertexAt(index, new Point2d(vertex.Position.X, vertex.Position.Y), vertex.Bulge, 0, 0);
            index++;
        }

        if (index < 2)
        {
            lw.Dispose();
            return ObjectId.Null;
        }

        lw.Closed = source.Closed;
        try
        {
            lw.Layer = source.Layer;
        }
        catch
        {
            lw.Layer = "0";
        }

        try
        {
            lw.Color = source.Color;
        }
        catch
        {
            // ignore
        }

        try
        {
            lw.Linetype = string.IsNullOrWhiteSpace(source.Linetype) ? "ByLayer" : source.Linetype;
        }
        catch
        {
            lw.Linetype = "ByLayer";
        }

        try
        {
            lw.LineWeight = source.LineWeight;
        }
        catch
        {
            // ignore
        }

        var ownerId = source.OwnerId;
        var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForWrite);
        var newId = owner.AppendEntity(lw);
        tr.AddNewlyCreatedDBObject(lw, true);
        source.Erase();
        return newId;
    }

    private static double? PromptDouble(Editor ed, string message, double defaultValue)
    {
        var options = new PromptDoubleOptions(message)
        {
            DefaultValue = defaultValue,
            UseDefaultValue = true,
            AllowNegative = false,
            AllowZero = true
        };

        var result = ed.GetDouble(options);
        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static string? PromptString(Editor ed, string message, string defaultValue)
    {
        var options = new PromptStringOptions(message)
        {
            DefaultValue = defaultValue,
            UseDefaultValue = true,
            AllowSpaces = false
        };

        var result = ed.GetString(options);
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static void DrawAxis(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        ObjectId sourcePolylineId = default,
        short axisColorIndex = DrawingColorDefaults.Axis) =>
        RoadDrawing.DrawAxis(tr, modelSpace, axis, sourcePolylineId, axisColorIndex);

    private static int DrawStationLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        double interval,
        double tickLength,
        double textHeight,
        string prefix) =>
        RoadDrawing.DrawStationLabels(tr, modelSpace, axis, interval, tickLength, textHeight, prefix);

    private static void PrintAxisReport(
        Editor ed,
        RoadAxis axis,
        int labelCount,
        int radiusCount = 0,
        int segmentCount = 0,
        int nodeCount = 0)
    {
        ed.WriteMessage($"\nTCM-INZINJERING: Osovina '{axis.Name}' kreirana.");
        ed.WriteMessage($"\n  Pocetna stacionaza : {RoadDrawing.FormatStation(axis.StartStation, string.Empty)}");
        ed.WriteMessage($"\n  Krajnja stacionaza  : {RoadDrawing.FormatStation(axis.Elements[^1].EndStation, string.Empty)}");
        ed.WriteMessage($"\n  Ukupna duzina       : {axis.TotalLength:F2} m");
        ed.WriteMessage($"\n  Broj elemenata      : {axis.Elements.Count}");

        var index = 1;
        foreach (var element in axis.Elements)
        {
            var type = element.Type == AlignmentElementType.Tangent ? "Pravac" : "Luk";
            ed.WriteMessage(
                $"\n  {index,2}. {type,-6} L={element.Length,8:F2} m  STA {RoadDrawing.FormatStation(element.StartStation, string.Empty)} - {RoadDrawing.FormatStation(element.EndStation, string.Empty)}  R={element.Radius:F2}");
            index++;
        }

        if (labelCount > 0)
        {
            ed.WriteMessage($"\n  Iscrtano oznaka stacionaze: {labelCount}");
        }

        if (radiusCount > 0)
        {
            ed.WriteMessage($"\n  Iscrtano oznaka radijusa: {radiusCount}");
        }

        if (segmentCount > 0)
        {
            ed.WriteMessage($"\n  Iscrtano oznaka segmenata: {segmentCount}");
        }

        if (nodeCount > 0)
        {
            ed.WriteMessage($"\n  Iscrtano tabela cvorova (T1…): {nodeCount}");
        }
    }
}
