using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class TerrainVolumeConfidenceDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Func<string, string, double, int, double, double, bool, TerrainVolumeResult?> _calculate;
    private readonly Action<TerrainVolumeResult> _showMap;
    private readonly Func<TerrainVolumeResult, string?> _saveReport;

    private TerrainVolumeResult? _lastResult;

    public TerrainVolumeConfidenceDialog(
        IReadOnlyList<string> surfaceNames,
        string? defaultBase,
        string? defaultComparison,
        Func<string, string, double, int, double, double, bool, TerrainVolumeResult?> calculate,
        Action<TerrainVolumeResult> showMap,
        Func<TerrainVolumeResult, string?> saveReport)
    {
        _calculate = calculate;
        _showMap = showMap;
        _saveReport = saveReport;
        InitializeComponent();

        foreach (var name in surfaceNames)
        {
            BaseBox.Items.Add(name);
            ComparisonBox.Items.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(defaultBase) &&
            surfaceNames.Any(n => string.Equals(n, defaultBase, StringComparison.OrdinalIgnoreCase)))
        {
            BaseBox.SelectedItem = surfaceNames.First(n =>
                string.Equals(n, defaultBase, StringComparison.OrdinalIgnoreCase));
        }
        else if (BaseBox.Items.Count > 0)
        {
            BaseBox.SelectedIndex = 0;
        }

        if (!string.IsNullOrWhiteSpace(defaultComparison) &&
            surfaceNames.Any(n => string.Equals(n, defaultComparison, StringComparison.OrdinalIgnoreCase)))
        {
            ComparisonBox.SelectedItem = surfaceNames.First(n =>
                string.Equals(n, defaultComparison, StringComparison.OrdinalIgnoreCase));
        }
        else if (ComparisonBox.Items.Count > 1)
        {
            ComparisonBox.SelectedIndex = 1;
        }
        else if (ComparisonBox.Items.Count > 0)
        {
            ComparisonBox.SelectedIndex = 0;
        }
    }

    private void OnCalculate(object sender, RoutedEventArgs e)
    {
        if (BaseBox.SelectedItem is not string baseName ||
            ComparisonBox.SelectedItem is not string cmpName)
        {
            MessageBox.Show(this, "Izaberite baza i poredjenje teren.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(baseName, cmpName, StringComparison.OrdinalIgnoreCase))
        {
            if (MessageBox.Show(this,
                    "Baza i poredjenje su isti teren. Nastaviti? (ocekivano iskop≈nasip≈0)",
                    "TCM-ROADS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (!TryParsePositive(GridStepBox.Text, 5.0, out var gridStep) ||
            !TryParseInt(SectionCountBox.Text, 12, out var sections) ||
            !TryParsePositive(SwellBox.Text, 1.0, out var swell) ||
            !TryParsePositive(ShrinkBox.Text, 1.0, out var shrink))
        {
            MessageBox.Show(this, "Proverite brojcane parametre (grid, sekcije, faktori).",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pickBoundary = BoundModeBox.SelectedIndex == 1;
        TerrainVolumeResult? result;
        Visibility = Visibility.Hidden;
        try
        {
            result = _calculate(baseName, cmpName, gridStep, sections, swell, shrink, pickBoundary);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Visibility = Visibility.Visible;
            Activate();
        }

        if (result is null)
        {
            return;
        }

        _lastResult = result;
        ApplyResult(result);
    }

    private void ApplyResult(TerrainVolumeResult r)
    {
        if (!string.IsNullOrWhiteSpace(r.Warning) &&
            r.Tin.CutVolume <= 0 && r.Tin.FillVolume <= 0)
        {
            ConfidenceText.Text = r.Warning;
            SetBanner("#FFF3E0", "#FFCC80");
            ResultsGrid.ItemsSource = null;
            DetailText.Text = string.Empty;
            return;
        }

        ResultsGrid.ItemsSource = new[]
        {
            Row(r.Tin),
            Row(r.Grid),
            Row(r.Sections),
            new
            {
                Method = "TIN + faktori",
                Cut = r.AdjustedCut.ToString("0.000", Inv),
                Fill = r.AdjustedFill.ToString("0.000", Inv),
                Net = r.AdjustedNet.ToString("0.000", Inv),
                CutArea = "—",
                FillArea = "—"
            }
        };

        ConfidenceText.Text =
            $"Poverenje: {r.ConfidenceLevel}  |  mean {r.MeanRelativeErrorPercent:0.00}%  ·  " +
            $"max {r.MaxRelativeErrorPercent:0.00}%\n{r.ConfidenceNote}";
        if (r.ConfidenceLevel.StartsWith("Vis", StringComparison.OrdinalIgnoreCase))
        {
            SetBanner("#E8F5E9", "#A5D6A7");
        }
        else if (r.ConfidenceLevel.StartsWith("Sre", StringComparison.OrdinalIgnoreCase))
        {
            SetBanner("#FFF8E1", "#FFE082");
        }
        else
        {
            SetBanner("#FFEBEE", "#EF9A9A");
        }

        DetailText.Text =
            $"Baza „{r.BaseName}“ → poredjenje „{r.ComparisonName}“  |  " +
            $"AOI {r.MinX:0.##}…{r.MaxX:0.##} × {r.MinY:0.##}…{r.MaxY:0.##}  |  " +
            $"grid {r.GridStep:0.##} m, sekcija {r.SectionCount}, " +
            $"swell×{r.SwellFactor:0.##}, shrink×{r.ShrinkFactor:0.##}" +
            (string.IsNullOrWhiteSpace(r.Warning) ? string.Empty : "\n" + r.Warning);
    }

    private static object Row(TerrainVolumeMethodResult m) =>
        new
        {
            Method = m.MethodName,
            Cut = m.CutVolume.ToString("0.000", Inv),
            Fill = m.FillVolume.ToString("0.000", Inv),
            Net = m.NetVolume.ToString("0.000", Inv),
            CutArea = m.CutArea.ToString("0.00", Inv),
            FillArea = m.FillArea.ToString("0.00", Inv)
        };

    private void SetBanner(string bg, string border)
    {
        ConfidenceBanner.Background = (Brush)new BrushConverter().ConvertFromString(bg)!;
        ConfidenceBanner.BorderBrush = (Brush)new BrushConverter().ConvertFromString(border)!;
    }

    private void OnShowMap(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            MessageBox.Show(this, "Prvo izracunajte zapreminu.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Visibility = Visibility.Hidden;
        try
        {
            _showMap(_lastResult);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Visibility = Visibility.Visible;
            Activate();
        }
    }

    private void OnSaveReport(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            MessageBox.Show(this, "Prvo izracunajte zapreminu.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var path = _saveReport(_lastResult);
            if (path is null)
            {
                MessageBox.Show(this, "Izvestaj nije snimljen.", "TCM-ROADS",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this, $"Izvestaj snimljen:\n{path}", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private static bool TryParsePositive(string? text, double fallback, out double value)
    {
        var raw = (text ?? string.Empty).Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, Inv, out value) || value <= 0)
        {
            value = fallback;
            return false;
        }

        return true;
    }

    private static bool TryParseInt(string? text, int fallback, out int value)
    {
        if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, Inv, out value) ||
            value < 2)
        {
            value = fallback;
            return false;
        }

        return true;
    }
}
