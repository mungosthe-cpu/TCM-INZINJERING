using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed class RoadCommands
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
            var polylineId = SelectPolyline(ed);
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

            var dialog = new Plo2TanDialog(polylineLength);
            var accepted = AcApp.ShowModalWindow(dialog) == true;
            if (!accepted)
            {
                ed.WriteMessage("\nTCM-INZINJERING: komanda otkazana.");
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var polyline = (Polyline)tr.GetObject(polylineId, OpenMode.ForRead);
            var axis = PolylineToTangentConverter.Convert(
                polyline,
                dialog.CurveRadius,
                dialog.StartStation,
                dialog.AxisName);
            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            DrawAxis(tr, modelSpace, axis);
            var labelCount = RoadDrawing.DrawStationLabels(tr, modelSpace, axis, dialog.StationOptions);
            var radiusCount = RoadDrawing.DrawRadiusLabels(tr, modelSpace, axis, dialog.TextHeight);
            RoadDrawing.SaveAxisMetadata(tr, db, axis, dialog.StationOptions, dialog.CurveRadius);
            tr.Commit();

            PrintAxisReport(ed, axis, labelCount, radiusCount);
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
        var db = doc.Database;
        var ed = doc.Editor;

        var polylineId = SelectPolyline(ed);
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

        var labelCount = RoadDrawing.DrawStationLabels(tr, modelSpace, axis, interval.Value, 2.0, 2.5, "STA ");
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
        var ed = doc.Editor;

        var polylineId = SelectPolyline(ed);
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

    private static ObjectId SelectPolyline(Editor ed)
    {
        var options = new PromptEntityOptions("\nIzaberite polylinu osovine: ");
        options.SetRejectMessage("\nMora biti polylinija (LWPOLYLINE).");
        options.AddAllowedClass(typeof(Polyline), true);
        var result = ed.GetEntity(options);
        return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
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

    private static void DrawAxis(Transaction tr, BlockTableRecord modelSpace, RoadAxis axis) =>
        RoadDrawing.DrawAxis(tr, modelSpace, axis);

    private static int DrawStationLabels(
        Transaction tr,
        BlockTableRecord modelSpace,
        RoadAxis axis,
        double interval,
        double tickLength,
        double textHeight,
        string prefix) =>
        RoadDrawing.DrawStationLabels(tr, modelSpace, axis, interval, tickLength, textHeight, prefix);

    private static void PrintAxisReport(Editor ed, RoadAxis axis, int labelCount, int radiusCount = 0)
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
    }
}
