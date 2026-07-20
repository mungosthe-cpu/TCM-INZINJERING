using System.IO;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    /// <summary>
    /// Georeferencirana podloga: Autodesk Esri mapa ili nezavisni WMS/ArcGIS/lokalni raster.
    /// </summary>
    [CommandMethod("TCMTERMAP", CommandFlags.Modal)]
    public void InsertTerrainBasemap()
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
            while (true)
            {
                var geoStatus = Terrain.BasemapGeoHelper.DescribeGeoStatus(db);
                var dialog = new BasemapSettingsDialog(geoStatus);
                var shown = AcApp.ShowModalWindow(dialog);

                if (dialog.Tag is "AUTOGEO")
                {
                    var msg = Terrain.BasemapGeoAssignService.AssignFromDrawing(db);
                    ed.WriteMessage($"\nTCM-ROADS: {msg}");
                    System.Windows.MessageBox.Show(
                        msg, "TCM-ROADS — Geolokacija iz X/Y",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    continue;
                }

                if (dialog.Tag is string hostCmd &&
                    (hostCmd == "GEOGRAPHICLOCATION" || hostCmd == "MAPCSASSIGN"))
                {
                    ed.WriteMessage($"\nTCM-ROADS: Pokrecem {hostCmd}…");
                    try
                    {
                        ed.Command("_." + hostCmd);
                    }
                    catch
                    {
                        doc.SendStringToExecute(hostCmd + " ", true, false, false);
                    }

                    continue;
                }

                if (shown != true || dialog.Settings is null)
                {
                    return;
                }

                RunBasemap(ed, db, dialog.Settings);
                return;
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
    }

    private static void RunBasemap(
        Editor ed,
        Autodesk.AutoCAD.DatabaseServices.Database db,
        BasemapSettings settings)
    {
        if (settings.Mode == BasemapMode.Autodesk)
        {
            Point2d? amin = null;
            Point2d? amax = null;
            if (settings.AutodeskAction == BasemapAutodeskAction.CaptureArea)
            {
                if (!TryGetBasemapArea(ed, "za ugradjivanje mape", out var min, out var max))
                {
                    return;
                }

                amin = min;
                amax = max;
            }

            Terrain.BasemapAutodeskService.Run(ed, settings, amin, amax);
            return;
        }

        // Nezavisni nacin
        Point2d minPt = default;
        Point2d maxPt = default;
        var localWithWorld = settings.ExternalSource == BasemapExternalSource.LocalFile &&
                             File.Exists(settings.LocalFilePath) &&
                             HasWorldFile(settings.LocalFilePath);
        if (!localWithWorld)
        {
            if (settings.AreaMode == BasemapAreaMode.Viewport)
            {
                GetViewportBounds(ed, out minPt, out maxPt);
            }
            else if (!TryGetBasemapArea(ed, "podloge", out minPt, out maxPt))
            {
                return;
            }
        }

        var progressWin = new CommandProgressWindow("TCM-ROADS — Podloga");
        progressWin.Show();
        var progress = progressWin.AsProgress();
        try
        {
            progress.Report((5, "Pripremam podlogu…"));
            var download = Terrain.BasemapExternalService.Prepare(
                db, settings, minPt, maxPt, progress);

            progress.Report((90, "Umecem RasterImage…"));
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Terrain.BasemapRasterAttachmentService.Attach(
                    tr, db, download, settings.OpacityPercent);
                tr.Commit();
            }

            progress.Report((100, "Gotovo."));
            ed.Regen();
            ed.WriteMessage(
                $"\nTCM-ROADS: Podloga umetnuta na lejer {Terrain.BasemapRasterAttachmentService.LayerName}.\n" +
                $"  Izvor: {download.SourceLabel}\n" +
                $"  {download.Attribution}");
        }
        finally
        {
            try
            {
                progressWin.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool TryGetBasemapArea(
        Editor ed,
        string purpose,
        out Point2d min,
        out Point2d max)
    {
        min = default;
        max = default;
        var first = ed.GetPoint($"\nPrvi ugao oblasti {purpose}: ");
        if (first.Status == PromptStatus.None)
        {
            return false;
        }

        if (first.Status != PromptStatus.OK)
        {
            return false;
        }

        var second = ed.GetCorner(new PromptCornerOptions(
            $"\nSuprotni ugao oblasti {purpose}: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            return false;
        }

        min = new Point2d(
            Math.Min(first.Value.X, second.Value.X),
            Math.Min(first.Value.Y, second.Value.Y));
        max = new Point2d(
            Math.Max(first.Value.X, second.Value.X),
            Math.Max(first.Value.Y, second.Value.Y));
        return max.X > min.X && max.Y > min.Y;
    }

    private static void GetViewportBounds(Editor ed, out Point2d min, out Point2d max)
    {
        using var view = ed.GetCurrentView();
        var halfW = view.Width * 0.5;
        var halfH = view.Height * 0.5;
        min = new Point2d(view.CenterPoint.X - halfW, view.CenterPoint.Y - halfH);
        max = new Point2d(view.CenterPoint.X + halfW, view.CenterPoint.Y + halfH);
    }

    private static bool HasWorldFile(string imagePath)
    {
        var stem = Path.ChangeExtension(imagePath, null) ?? imagePath;
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        string wf;
        if (ext == ".jpg" || ext == ".jpeg")
        {
            wf = stem + ".jgw";
        }
        else if (ext == ".png")
        {
            wf = stem + ".pgw";
        }
        else if (ext == ".tif" || ext == ".tiff")
        {
            wf = stem + ".tfw";
        }
        else if (ext == ".bmp")
        {
            wf = stem + ".bpw";
        }
        else
        {
            wf = stem + ".wld";
        }

        return File.Exists(wf) || File.Exists(stem + ".wld") || File.Exists(stem + ".tfw");
    }
}
