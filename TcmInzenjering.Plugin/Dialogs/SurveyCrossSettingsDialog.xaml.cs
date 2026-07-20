using System.Globalization;
using System.Windows;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public sealed record SurveyCrossSettings(
    double SpacingX,
    double SpacingY,
    double CrossLength,
    double TextHeight,
    bool LabelEast,
    bool LabelNorth,
    bool GroupedLabels,
    int Decimals,
    string LayerName,
    short LayerColorAci,
    string FontFileName,
    short TextColorAci);

public partial class SurveyCrossSettingsDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string DefaultFont = "arial.ttf";

    private short _layerColorAci = 3;
    private short _textColorAci = 3;

    public SurveyCrossSettings? Settings { get; private set; }

    public SurveyCrossSettingsDialog()
    {
        InitializeComponent();

        var fonts = StationFontCatalog.Load();
        FontBox.ItemsSource = fonts;
        FontBox.DisplayMemberPath = nameof(StationFontOption.DisplayName);
        var defaultOption = fonts.FirstOrDefault(f =>
            string.Equals(f.FileName, DefaultFont, StringComparison.OrdinalIgnoreCase));
        FontBox.SelectedItem = defaultOption;
        FontBox.Text = defaultOption?.DisplayName ?? DefaultFont;

        AciColorHelper.ApplyToButton(LayerColorBtn, _layerColorAci);
        AciColorHelper.ApplyToButton(TextColorBtn, _textColorAci);

        Loaded += (_, _) =>
        {
            SpacingXBox.Focus();
            SpacingXBox.SelectAll();
        };
    }

    private void OnPickLayerColor(object sender, RoutedEventArgs e) =>
        AciColorHelper.ShowPicker(LayerColorBtn, _layerColorAci, aci =>
        {
            _layerColorAci = aci;
            AciColorHelper.ApplyToButton(LayerColorBtn, aci);
        });

    private void OnPickTextColor(object sender, RoutedEventArgs e) =>
        AciColorHelper.ShowPicker(TextColorBtn, _textColorAci, aci =>
        {
            _textColorAci = aci;
            AciColorHelper.ApplyToButton(TextColorBtn, aci);
        });

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (!TryPositive(SpacingXBox.Text, out var spacingX) ||
            !TryPositive(SpacingYBox.Text, out var spacingY) ||
            !TryPositive(CrossLengthBox.Text, out var crossLength) ||
            !TryPositive(TextHeightBox.Text, out var textHeight))
        {
            MessageBox.Show(this,
                "Razmaci, duzina krsta i visina teksta moraju biti pozitivni brojevi.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(DecimalsBox.Text.Trim(), NumberStyles.Integer, Inv, out var decimals) ||
            decimals < 0 || decimals > 6)
        {
            MessageBox.Show(this, "Broj decimala mora biti ceo broj od 0 do 6.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var layer = (LayerBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(layer) || layer.IndexOfAny("<>/\\\":;?*|,=`".ToCharArray()) >= 0)
        {
            MessageBox.Show(this, "Unesite ispravno ime lejera.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedFont = FontBox.SelectedItem as StationFontOption;
        var fontFile = StationFontCatalog.ResolveFileName(
            selectedFont?.FileName ?? FontBox.Text);
        if (string.IsNullOrWhiteSpace(fontFile))
        {
            fontFile = DefaultFont;
        }

        Settings = new SurveyCrossSettings(
            spacingX, spacingY, crossLength, textHeight,
            LabelEastBox.IsChecked == true,
            LabelNorthBox.IsChecked == true,
            GroupedLabelBox.IsChecked == true,
            decimals, layer, _layerColorAci, fontFile, _textColorAci);
        DialogResult = true;
    }

    private static bool TryPositive(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float, Inv, out value) && value > 0;
}
