using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Dialogs;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    [CommandMethod("TCMPROJEKAT", CommandFlags.Modal)]
    public void OpenProjectBrowser()
    {
#if BRICSCAD
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        doc?.Editor.WriteMessage("\nTCM-ROADS: TCMPROJEKAT zahteva WPF dijalog (AutoCAD).");
#else
        try
        {
            var dialog = new ProjectBrowserDialog();
            AcApp.ShowModalWindow(dialog);
        }
        catch (System.Exception ex)
        {
            var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\nTCM-ROADS greska: {ex.Message}");
        }
#endif
    }
}
