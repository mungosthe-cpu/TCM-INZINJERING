using System.Windows;
using System.Windows.Controls;
using TcmInzenjering.Plugin.Update;

namespace TcmInzenjering.Plugin.Dialogs;

/// <summary>Prozor kada postoji nova verzija — sa opcijom provere pri startu.</summary>
public partial class UpdateAvailableDialog : Window
{
    public bool StartDownload { get; private set; }
    public bool CheckOnStartup { get; private set; } = true;

    public UpdateAvailableDialog(string currentVersion, string latestVersion, string? releaseNotes)
    {
        InitializeComponent();
        VersionText.Text =
            $"Dostupna je nova verzija {latestVersion}.\nTrenutna verzija: {currentVersion}.";
        if (!string.IsNullOrWhiteSpace(releaseNotes))
        {
            NotesText.Text = "Novo u ovoj verziji:\n" + releaseNotes;
            NotesText.Visibility = Visibility.Visible;
        }

        CheckOnStartupBox.IsChecked = UpdatePreferences.CheckOnStartup;
    }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        CheckOnStartup = CheckOnStartupBox.IsChecked == true;
        StartDownload = true;
        DialogResult = true;
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        CheckOnStartup = CheckOnStartupBox.IsChecked == true;
        StartDownload = false;
        DialogResult = true;
    }
}
