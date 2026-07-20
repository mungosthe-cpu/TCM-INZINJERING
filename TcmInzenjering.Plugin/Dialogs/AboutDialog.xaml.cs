using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using TcmInzenjering.Plugin.Update;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        VersionText.Text = $"Verzija {PluginInfo.Version}";
        AuthorText.Text = PluginInfo.AuthorName;
        PhoneText.Text = PluginInfo.AuthorPhone;
        EmailText.Text = PluginInfo.AuthorEmail;
        UpdatePreferences.Load();
        CheckOnStartupBox.IsChecked = UpdatePreferences.CheckOnStartup;
        TryLoadLogo();
    }

    private void OnStartupPrefChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdatePreferences.Save(CheckOnStartupBox.IsChecked == true);
    }

    private void TryLoadLogo()
    {
        foreach (var path in GetLogoPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                LogoImage.Source = image;
                return;
            }
            catch
            {
                // Probaj sledeću putanju.
            }
        }
    }

    private static IEnumerable<string> GetLogoPaths()
    {
        var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrWhiteSpace(dllDir))
        {
            yield return Path.Combine(dllDir, "Icons", "TCM Logo.png");
            var contents = Directory.GetParent(dllDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(contents))
            {
                yield return Path.Combine(contents, "Icons", "TCM Logo.png");
                yield return Path.Combine(contents, "net8", "Icons", "TCM Logo.png");
            }
        }

        var repoIcons = Path.GetFullPath(Path.Combine(
            dllDir ?? AppContext.BaseDirectory,
            "..", "..", "..", "..", "ICONS", "TCM Logo.png"));
        yield return repoIcons;
    }

    private void OnEmailClick(object sender, RoutedEventArgs e) =>
        OpenExternal($"mailto:{PluginInfo.AuthorEmail}");

    private void OnGitHubClick(object sender, RoutedEventArgs e) =>
        OpenExternal(PluginInfo.ReleasesPageUrl);

    private void OnCheckUpdate(object sender, RoutedEventArgs e) =>
        UpdateUi.CheckAndNotify(owner: this);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // Ako sistem nema podrazumevani program, ne rušimo dijalog.
        }
    }
}
