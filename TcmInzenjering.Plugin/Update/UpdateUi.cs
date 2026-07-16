using TcmInzenjering.Plugin;
#if !BRICSCAD
using System.Windows;
#endif

namespace TcmInzenjering.Plugin.Update;

/// <summary>
/// Obaveštava korisnika o nadogradnji preko prozora (ne komandne linije).
/// </summary>
internal static class UpdateUi
{
#if !BRICSCAD
    private static Window? _owner;

    public static void CheckAndNotify(Window? owner = null)
    {
        _owner = owner;
#else
    public static void CheckAndNotify(object? owner = null)
    {
        _ = owner;
#endif
        UpdateCheckResult result;
        try
        {
            result = UpdateChecker.CheckForUpdates(forceRefresh: true);
        }
        catch (Exception ex)
        {
            ShowInfo("Provera nadogradnje nije uspela.\n\n" + ex.Message);
            return;
        }

        if (!result.CheckSucceeded)
        {
            ShowInfo(
                "Provera nadogradnje nije uspela.\n\n" +
                (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Nepoznata greška." : result.ErrorMessage) +
                "\n\nManifest:\n" + PluginInfo.UpdateManifestUrl);
            return;
        }

        if (!result.UpdateAvailable)
        {
            ShowInfo($"Imate najnoviju verziju.\n\nTrenutna verzija: {result.CurrentVersion}");
            return;
        }

        PromptAvailableUpdate(result);
    }

    /// <summary>Jedna potvrda + pokretanje preuzimanja (bez drugog Yes/No u PluginUpdater).</summary>
    public static void PromptAvailableUpdate(UpdateCheckResult result)
    {
        if (!result.UpdateAvailable)
        {
            return;
        }

        var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
            ? string.Empty
            : "\n\nNovo u ovoj verziji:\n" + result.ReleaseNotes;

        if (!AskYesNo(
                $"Dostupna je nova verzija {result.LatestVersion}.\n" +
                $"Trenutna verzija: {result.CurrentVersion}." +
                notes +
                "\n\nPreuzimanje ide u posebnom prozoru — AutoCAD možete nastaviti da koristite.\n" +
                "Kad zatvorite AutoCAD, instalacija se automatski završava.\n\n" +
                "Pokrenuti preuzimanje?"))
        {
            return;
        }

        if (!PluginUpdater.TryStartUpdate(result, out var message))
        {
            ShowInfo(message);
            return;
        }

        ShowInfo(message);
    }

    private static void ShowInfo(string message)
    {
#if BRICSCAD
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager
            .MdiActiveDocument?.Editor.WriteMessage("\nTCM-INZINJERING: " + message.Replace('\n', ' '));
#else
        if (_owner is not null)
        {
            MessageBox.Show(
                _owner,
                message,
                "TCM-INŽINJERING — Nadogradnja",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                message,
                "TCM-INŽINJERING — Nadogradnja",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
#endif
    }

    private static bool AskYesNo(string message)
    {
#if BRICSCAD
        var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
        {
            return false;
        }

        var opts = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions("\n" + message.Replace('\n', ' ') + " [Da/Ne] <Ne>: ")
        {
            AllowNone = true
        };
        opts.Keywords.Add("Da");
        opts.Keywords.Add("Ne");
        opts.Keywords.Default = "Ne";
        var res = ed.GetKeywords(opts);
        return res.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK &&
               string.Equals(res.StringResult, "Da", StringComparison.OrdinalIgnoreCase);
#else
        var result = _owner is not null
            ? MessageBox.Show(
                _owner,
                message,
                "TCM-INŽINJERING — Nadogradnja",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes)
            : MessageBox.Show(
                message,
                "TCM-INŽINJERING — Nadogradnja",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes;
#endif
    }
}
