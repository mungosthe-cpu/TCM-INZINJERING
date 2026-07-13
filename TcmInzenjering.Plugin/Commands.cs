using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Update;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin;

public sealed class Commands
{
    [CommandMethod("TCMHELLO", CommandFlags.Modal)]
    public void Hello()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        ed.WriteMessage("\nTCM-INZINJERING: Zdravo! Plugin radi ispravno.");
    }

    [CommandMethod("TCMINFO", CommandFlags.Modal)]
    public void Info()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        ed.WriteMessage($"\nTCM-INZINJERING v{PluginInfo.Version} - AutoCAD/BricsCAD plugin za puteve i osovinsku geometriju.");
        ed.WriteMessage($"\n  Provera nadogradnje: TCMUPDATE");
        ed.WriteMessage($"\n  GitHub: {PluginInfo.ReleasesPageUrl}");
    }

    [CommandMethod("TCMRIBBON", CommandFlags.Modal)]
    public void RefreshRibbon()
    {
        PluginApplication.EnsureRibbon();
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        doc.Editor.WriteMessage("\nTCM-INZINJERING: Ribbon tab je osvezen.");
    }
}
