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

        var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
            ? string.Empty
            : "\n\nNovo u ovoj verziji:\n" + result.ReleaseNotes;

        if (!AskYesNo(
                $"Dostupna je nova verzija {result.LatestVersion}.\n" +
                $"Trenutna verzija: {result.CurrentVersion}." +
                notes +
                "\n\nPokrenuti preuzimanje i instalaciju?"))
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
        return true;
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
