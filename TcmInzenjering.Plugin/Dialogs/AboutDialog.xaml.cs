using System.Diagnostics;
using System.Windows;
using TcmInzenjering.Plugin.Update;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        VersionText.Text = $"Verzija {PluginInfo.Version}";
        AuthorText.Text = $"{PluginInfo.AuthorName}, {PluginInfo.AuthorCity}";
        PhoneText.Text = PluginInfo.AuthorPhone;
        EmailText.Text = PluginInfo.AuthorEmail;
    }

    private void OnEmailClick(object sender, RoutedEventArgs e) =>
        OpenExternal($"mailto:{PluginInfo.AuthorEmail}");

    private void OnFacebookClick(object sender, RoutedEventArgs e) =>
        OpenExternal(PluginInfo.AuthorFacebookUrl);

    private void OnGitHubClick(object sender, RoutedEventArgs e) =>
        OpenExternal(PluginInfo.ReleasesPageUrl);

    private void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        Close();
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager
            .MdiActiveDocument?.SendStringToExecute("TCMUPDATE ", true, false, false);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // Ako sistem nema podrazumevani program, ne rusimo dijalog.
        }
    }
}
