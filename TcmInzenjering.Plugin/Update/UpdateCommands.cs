using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Update;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin;

public sealed class UpdateCommands
{
    [CommandMethod("TCMUPDATE", CommandFlags.Modal)]
    public void CheckForUpdates()
    {
#if BRICSCAD
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        doc?.Editor.WriteMessage("\nTCM-INZINJERING: provera nadogradnje...");
        UpdateUi.CheckAndNotify(owner: null);
#else
        // Rezultat u prozoru — bez ispisa u AutoCAD komandnoj liniji.
        UpdateUi.CheckAndNotify(owner: null);
#endif
    }

    internal static void CheckForUpdatesOnStartup()
    {
        try
        {
            var result = UpdateChecker.CheckForUpdates();
            if (!result.CheckSucceeded || !result.UpdateAvailable)
            {
                return;
            }

            // Tiha napomena na startu — bez komandne linije (korisnik pokreće TCMUPDATE ili Info).
            _ = result;
        }
        catch
        {
            // Provera nadogradnje ne sme da blokira ucitavanje plugina.
        }
    }
}
