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
#if NET8_0_OR_GREATER
        try
        {
            var dialog = new Dialogs.AboutDialog();
            AcApp.ShowModalWindow(dialog);
            return;
        }
        catch
        {
            // Ako WPF prozor ne moze da se otvori, ispisujemo info u komandnu liniju.
        }
#endif
        ed.WriteMessage($"\nTCM-INZINJERING v{PluginInfo.Version} - AutoCAD/BricsCAD plugin za puteve i osovinsku geometriju.");
        ed.WriteMessage($"\n  Autor   : {PluginInfo.AuthorName}, {PluginInfo.AuthorCity}");
        ed.WriteMessage($"\n  Telefon : {PluginInfo.AuthorPhone}");
        ed.WriteMessage($"\n  E-mail  : {PluginInfo.AuthorEmail}");
        ed.WriteMessage($"\n  Facebook: {PluginInfo.AuthorFacebookUrl}");
        ed.WriteMessage($"\n  Provera nadogradnje: TCMUPDATE");
        ed.WriteMessage($"\n  GitHub: {PluginInfo.ReleasesPageUrl}");
    }

    [CommandMethod("TCMRIBBON", CommandFlags.Modal)]
    public void RefreshRibbon()
    {
#if !BRICSCAD
        PluginApplication.EnsureRibbon();
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        doc.Editor.WriteMessage("\nTCM-INZINJERING: Ribbon tab je osvezen.");
#else
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        doc.Editor.WriteMessage("\nTCM-INZINJERING: Ribbon nije dostupan u BricsCAD verziji. Koristite komande TCMPLO2TAN, TCMSTACOZN, TCMUPDATE.");
#endif
    }

    [CommandMethod("TCMSTACFONT", CommandFlags.Modal)]
    public void ConfigureStationFont()
    {
#if NET48
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nTCM-INZINJERING: Podesavanje fonta stacionaze je dostupno u AutoCAD 2025+ verziji plugina.");
        return;
#else
        var dialog = new Dialogs.StationFontDialog();
        if (AcApp.ShowModalWindow(dialog) != true)
        {
            return;
        }

        Roads.StationFontPreferences.Save(dialog.SelectedFontFile);
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        doc?.Editor.WriteMessage($"\nTCM-INZINJERING: Font stacionaze: {dialog.SelectedFontFile}. Osvezi stacionaze (TCMSTACAZUR).");
#endif
    }

    [CommandMethod("TCMUNINSTALL", CommandFlags.Modal)]
    public void UninstallPlugin()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;

#if BRICSCAD
        if (ed is not null)
        {
            var opts = new PromptKeywordOptions("\nPotpuno obrisati TCM-INZINJERING iz AutoCAD-a? [Da/Ne] <Ne>: ")
            {
                AllowNone = true
            };
            opts.Keywords.Add("Da");
            opts.Keywords.Add("Ne");
            opts.Keywords.Default = "Ne";
            var res = ed.GetKeywords(opts);
            if (res.Status != PromptStatus.OK ||
                !string.Equals(res.StringResult, "Da", StringComparison.OrdinalIgnoreCase))
            {
                ed.WriteMessage("\nTCM-INZINJERING: Deinstalacija otkazana.");
                return;
            }
        }
#endif

        if (!PluginUninstaller.ConfirmAndStart(out var message))
        {
            ed?.WriteMessage($"\nTCM-INZINJERING: {message}");
            return;
        }

        ed?.WriteMessage($"\nTCM-INZINJERING: {message}");
    }
}
