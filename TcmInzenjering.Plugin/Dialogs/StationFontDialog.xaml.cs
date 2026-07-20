using System.Windows;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class StationFontDialog : Window
{
    public string SelectedFontFile { get; private set; } = StationFontPreferences.FontFileName;

    public StationFontDialog()
    {
        InitializeComponent();
        StationFontPreferences.Load();

        var fonts = StationFontCatalog.Load();
        FontBox.ItemsSource = fonts;
        FontBox.DisplayMemberPath = nameof(StationFontOption.DisplayName);
        FontBox.SelectedValuePath = nameof(StationFontOption.FileName);

        var current = fonts.FirstOrDefault(font =>
            string.Equals(font.FileName, StationFontPreferences.FontFileName, StringComparison.OrdinalIgnoreCase));
        FontBox.SelectedItem = current;
        FontBox.Text = current?.DisplayName ?? StationFontPreferences.FontFileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var selected = FontBox.SelectedItem as StationFontOption;
        var resolved = StationFontCatalog.ResolveFileName(selected?.FileName ?? FontBox.Text);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            MessageBox.Show(this, "Izaberite font.", "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedFontFile = resolved;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
