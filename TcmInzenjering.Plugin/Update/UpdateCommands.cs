using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Update;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin;

public sealed class UpdateCommands
{
    [CommandMethod("TCMUPDATE", CommandFlags.Modal)]
    public void CheckForUpdates()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        ed.WriteMessage("\nTCM-INZINJERING: provera nadogradnje...");

        var result = UpdateChecker.CheckForUpdates(forceRefresh: true);
        WriteResult(ed, result);

        if (!result.CheckSucceeded || !result.UpdateAvailable)
        {
            return;
        }

#if NET48
        var opts = new PromptKeywordOptions(
            $"\nPreuzeti i instalirati v{result.LatestVersion}? [Da/Ne] <Da>: ")
        {
            AllowNone = true
        };
        opts.Keywords.Add("Da");
        opts.Keywords.Add("Ne");
        opts.Keywords.Default = "Da";
        var res = ed.GetKeywords(opts);
        if (res.Status != PromptStatus.OK ||
            string.Equals(res.StringResult, "Ne", StringComparison.OrdinalIgnoreCase))
        {
            ed.WriteMessage("\nTCM-INZINJERING: Nadogradnja otkazana.");
            return;
        }
#endif

        ed.WriteMessage("\nTCM-INZINJERING: preuzimanje instalera...");
        if (!PluginUpdater.TryStartUpdate(result, out var message))
        {
            ed.WriteMessage($"\nTCM-INZINJERING: {message}");
            return;
        }

        ed.WriteMessage($"\nTCM-INZINJERING: {message}");
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

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage(
                $"\nTCM-INZINJERING: dostupna je nova verzija {result.LatestVersion} (trenutna {result.CurrentVersion}). Pokreni TCMUPDATE za instalaciju.");
        }
        catch
        {
            // Provera nadogradnje ne sme da blokira ucitavanje plugina.
        }
    }

    private static void WriteResult(Editor ed, UpdateCheckResult result)
    {
        ed.WriteMessage($"\nTCM-INZINJERING: trenutna verzija {result.CurrentVersion}.");

        if (!result.CheckSucceeded)
        {
            ed.WriteMessage($"\n  Provera nije uspela: {result.ErrorMessage}");
            ed.WriteMessage($"\n  Manifest: {PluginInfo.UpdateManifestUrl}");
            return;
        }

        if (!result.UpdateAvailable)
        {
            ed.WriteMessage("\n  Imate najnoviju verziju.");
            return;
        }

        ed.WriteMessage($"\n  Dostupna verzija: {result.LatestVersion}");
        if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            ed.WriteMessage($"\n  Napomene: {result.ReleaseNotes}");
        }

        ed.WriteMessage($"\n  Preuzimanje: {result.DownloadUrl}");
    }
}
