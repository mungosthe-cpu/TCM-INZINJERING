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
#endif
        UpdateUi.CheckAndNotify(owner: null);
    }

    /// <summary>
    /// Tiha provera na startu: ako postoji nova verzija, pita korisnika (jednom) da li da preuzme.
    /// </summary>
    internal static void CheckForUpdatesOnStartup()
    {
        try
        {
            var result = UpdateChecker.CheckForUpdates();
            if (!result.CheckSucceeded || !result.UpdateAvailable)
            {
                return;
            }

            // MessageBox mora sa AutoCAD UI threada (Idle), ne iz Task.Run.
            void OnIdle(object? sender, EventArgs e)
            {
                AcApp.Idle -= OnIdle;
                try
                {
                    UpdateUi.PromptAvailableUpdate(result);
                }
                catch
                {
                    // Provera nadogradnje ne sme da sreze start.
                }
            }

            AcApp.Idle += OnIdle;
        }
        catch
        {
            // Provera nadogradnje ne sme da blokira ucitavanje plugina.
        }
    }
}
