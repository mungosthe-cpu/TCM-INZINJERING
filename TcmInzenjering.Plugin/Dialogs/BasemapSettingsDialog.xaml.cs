using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Dialogs;

public enum BasemapMode
{
    Autodesk,
    External
}

public enum BasemapAutodeskAction
{
    Live,
    CaptureViewport,
    CaptureArea
}

public enum BasemapExternalSource
{
    ArcGisWorld,
    CustomUrl,
    LocalFile
}

public enum BasemapAreaMode
{
    Pick,
    Viewport
}

public sealed record BasemapSettings(
    BasemapMode Mode,
    string MapStyleTag,
    BasemapAutodeskAction AutodeskAction,
    BasemapExternalSource ExternalSource,
    string ServiceUrl,
    string WmsLayer,
    string LocalFilePath,
    BasemapAreaMode AreaMode,
    int ResolutionPx,
    byte OpacityPercent,
    BasemapDrawingCrs DrawingCrs);

public partial class BasemapSettingsDialog : Window
{
    public BasemapSettings? Settings { get; private set; }

    public BasemapSettingsDialog(string geoStatusText)
    {
        InitializeComponent();
        GeoStatusText.Text = geoStatusText;
        OpacitySlider.ValueChanged += (_, _) =>
            OpacityLabel.Text = $"{(int)OpacitySlider.Value}%";

        var prefs = BasemapPreferences.Current;
        ModeAutodeskRadio.IsChecked = prefs.LastMode == BasemapMode.Autodesk;
        ModeExternalRadio.IsChecked = prefs.LastMode == BasemapMode.External;
        SelectComboByTag(MapStyleBox, prefs.MapStyleTag);
        SelectComboByTag(AutodeskActionBox, prefs.AutodeskAction.ToString());
        SelectComboByTag(ExternalSourceBox, prefs.ExternalSource.ToString());
        ServiceUrlBox.Text = string.IsNullOrWhiteSpace(prefs.ServiceUrl)
            ? BasemapPreferences.DefaultArcGisWorldUrl
            : prefs.ServiceUrl;
        WmsLayerBox.Text = prefs.WmsLayer ?? string.Empty;
        LocalFileBox.Text = prefs.LocalFilePath ?? string.Empty;
        SelectComboByTag(AreaModeBox, prefs.AreaMode.ToString());
        SelectComboByTag(ResolutionBox, prefs.ResolutionPx.ToString());
        SelectComboByTag(CrsBox, prefs.DrawingCrs.ToString());
        OpacitySlider.Value = Math.Max(20, Math.Min(100, (int)prefs.OpacityPercent));
        OpacityLabel.Text = $"{(int)OpacitySlider.Value}%";

        UpdateModeVisibility();
        UpdateExternalFields();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) => UpdateModeVisibility();

    private void OnExternalSourceChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateExternalFields();

    private void UpdateModeVisibility()
    {
        // XAML Checked event okida tokom InitializeComponent, pre kreiranja grupa.
        if (ModeAutodeskRadio is null || AutodeskGroup is null || ExternalGroup is null)
        {
            return;
        }

        var autodesk = ModeAutodeskRadio.IsChecked == true;
        AutodeskGroup.Visibility = autodesk ? Visibility.Visible : Visibility.Collapsed;
        ExternalGroup.Visibility = autodesk ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateExternalFields()
    {
        // SelectionChanged okida tokom InitializeComponent, pre kreiranja ostalih polja.
        if (ExternalSourceBox is null || ServiceUrlBox is null || WmsLayerBox is null ||
            LocalFileBox is null || AreaModeBox is null || ResolutionBox is null)
        {
            return;
        }

        var tag = GetSelectedTag(ExternalSourceBox);
        var custom = tag == "CustomUrl";
        var local = tag == "LocalFile";
        ServiceUrlBox.IsEnabled = custom || tag == "ArcGisWorld";
        WmsLayerBox.IsEnabled = custom;
        LocalFileBox.IsEnabled = local;
        AreaModeBox.IsEnabled = !local;
        ResolutionBox.IsEnabled = !local;
        if (tag == "ArcGisWorld" &&
            (string.IsNullOrWhiteSpace(ServiceUrlBox.Text) ||
             ServiceUrlBox.Text.IndexOf("wms", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            ServiceUrlBox.Text = BasemapPreferences.DefaultArcGisWorldUrl;
        }
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Izaberite georeferenciranu sliku",
            Filter =
                "Raster|*.tif;*.tiff;*.jpg;*.jpeg;*.png;*.bmp|" +
                "GeoTIFF|*.tif;*.tiff|JPEG|*.jpg;*.jpeg|PNG|*.png|Svi fajlovi|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            LocalFileBox.Text = dlg.FileName;
        }
    }

    private void OnAutoGeo(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Settings = null;
        Tag = "AUTOGEO";
        Close();
    }

    private void OnAssignGeo(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Settings = null;
        Tag = "GEOGRAPHICLOCATION";
        Close();
    }

    private void OnAssignCrs(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Settings = null;
        Tag = "MAPCSASSIGN";
        Close();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var mode = ModeAutodeskRadio.IsChecked == true
            ? BasemapMode.Autodesk
            : BasemapMode.External;

        if (mode == BasemapMode.External)
        {
            var source = ParseEnum(GetSelectedTag(ExternalSourceBox), BasemapExternalSource.ArcGisWorld);
            if (source == BasemapExternalSource.LocalFile)
            {
                var path = (LocalFileBox.Text ?? string.Empty).Trim();
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "Izaberite postojeci lokalni raster fajl.",
                        "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                var url = (ServiceUrlBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    MessageBox.Show(this, "Unesite ispravan HTTP(S) URL servisa.",
                        "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        Settings = new BasemapSettings(
            mode,
            GetSelectedTag(MapStyleBox) ?? "EsriImagery",
            ParseEnum(GetSelectedTag(AutodeskActionBox), BasemapAutodeskAction.CaptureViewport),
            ParseEnum(GetSelectedTag(ExternalSourceBox), BasemapExternalSource.ArcGisWorld),
            (ServiceUrlBox.Text ?? string.Empty).Trim(),
            (WmsLayerBox.Text ?? string.Empty).Trim(),
            (LocalFileBox.Text ?? string.Empty).Trim(),
            ParseEnum(GetSelectedTag(AreaModeBox), BasemapAreaMode.Pick),
            int.TryParse(GetSelectedTag(ResolutionBox), out var res) ? res : 2048,
            (byte)Math.Max(20, Math.Min(100, (int)OpacitySlider.Value)),
            ParseEnum(GetSelectedTag(CrsBox), BasemapDrawingCrs.AutoGk));

        BasemapPreferences.Save(Settings);
        DialogResult = true;
    }

    private static string? GetSelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static void SelectComboByTag(ComboBox box, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }

    private static T ParseEnum<T>(string? text, T fallback) where T : struct =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) ? value : fallback;
}
