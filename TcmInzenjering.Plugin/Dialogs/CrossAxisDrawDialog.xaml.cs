using System.Globalization;
using System.Windows;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public enum CrossAxisDrawCloseAction
{
    Cancelled,
    Confirmed,
    PickStation,
    DrawMultipleInDrawing
}

public sealed class CrossAxisDrawDialogResult
{
    public CrossAxisDrawParameters Parameters { get; init; } = new();
}

public partial class CrossAxisDrawDialog : Window
{
    private readonly double _minStation;
    private readonly double _maxStation;

    public CrossAxisDrawCloseAction CloseAction { get; private set; } = CrossAxisDrawCloseAction.Cancelled;
    public CrossAxisDrawDialogResult Result { get; private set; } = new();

    public CrossAxisDrawDialog(
        double minStation,
        double maxStation,
        CrossAxisDrawParameters initial)
    {
        _minStation = minStation;
        _maxStation = maxStation;
        InitializeComponent();
        StationRangeText.Text = $"({minStation:0.000}, {maxStation:0.000})";
        StationBox.Text = initial.Station.ToString("0.000", CultureInfo.InvariantCulture);
        LeftWidthBox.Text = initial.LeftWidth.ToString("0.000", CultureInfo.InvariantCulture);
        RightWidthBox.Text = initial.RightWidth.ToString("0.000", CultureInfo.InvariantCulture);
        PrefixBox.Text = initial.Prefix;
        CounterStartBox.Text = initial.CounterStart.ToString(CultureInfo.InvariantCulture);
        AutoNamingRadio.IsChecked = initial.AutoNaming;
        FixedNamingRadio.IsChecked = !initial.AutoNaming;
        IncreasingRadio.IsChecked = initial.IncreasingNumbers;
        DecreasingRadio.IsChecked = !initial.IncreasingNumbers;
        FixedNameBox.Text = initial.FixedName;
        UpdateNamingModeUi();
    }

    public void SetStation(double station)
    {
        var clamped = Math.Max(_minStation, Math.Min(station, _maxStation));
        StationBox.Text = clamped.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private void OnNamingModeChanged(object sender, RoutedEventArgs e) => UpdateNamingModeUi();

    private void UpdateNamingModeUi()
    {
        if (!IsLoaded)
        {
            return;
        }

        var auto = AutoNamingRadio.IsChecked == true;
        PrefixBox.IsEnabled = auto;
        CounterStartBox.IsEnabled = auto;
        IncreasingRadio.IsEnabled = auto;
        DecreasingRadio.IsEnabled = auto;
        FixedNameBox.IsEnabled = !auto;
    }

    private void OnPickStation(object sender, RoutedEventArgs e)
    {
        if (!TryBuildParameters(out _))
        {
            return;
        }

        CloseAction = CrossAxisDrawCloseAction.PickStation;
        Result = BuildResult();
        DialogResult = false;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryBuildParameters(out var error))
        {
            MessageBox.Show(error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseAction = CrossAxisDrawCloseAction.Confirmed;
        Result = BuildResult();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseAction = CrossAxisDrawCloseAction.Cancelled;
        DialogResult = false;
        Close();
    }

    private void OnDrawMultiple(object sender, RoutedEventArgs e)
    {
        if (!TryBuildParameters(out var error))
        {
            MessageBox.Show(error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseAction = CrossAxisDrawCloseAction.DrawMultipleInDrawing;
        Result = BuildResult();
        DialogResult = false;
        Close();
    }

    private CrossAxisDrawDialogResult BuildResult() => new()
    {
        Parameters = BuildParametersFromUi()
    };

    private bool TryBuildParameters(out string error)
    {
        error = string.Empty;
        if (!TryParseDouble(StationBox.Text, out var station))
        {
            error = "Unesite ispravnu stacionažu.";
            return false;
        }

        if (station < _minStation - 1e-3 || station > _maxStation + 1e-3)
        {
            error = $"Stacionaža mora biti u opsegu {_minStation:0.###} – {_maxStation:0.###}.";
            return false;
        }

        if (!TryParseDouble(LeftWidthBox.Text, out var left) || left < 0.1)
        {
            error = "Širina levo mora biti ≥ 0.1 m.";
            return false;
        }

        if (!TryParseDouble(RightWidthBox.Text, out var right) || right < 0.1)
        {
            error = "Širina desno mora biti ≥ 0.1 m.";
            return false;
        }

        if (AutoNamingRadio.IsChecked == true)
        {
            if (!TryParseInt(CounterStartBox.Text, out var counter) || counter < 0)
            {
                error = "Početna vrednost brojača mora biti ceo broj ≥ 0.";
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(FixedNameBox.Text))
        {
            error = "Unesite fiksno ime poprečne ose.";
            return false;
        }

        return true;
    }

    private CrossAxisDrawParameters BuildParametersFromUi()
    {
        TryParseDouble(StationBox.Text, out var station);
        TryParseDouble(LeftWidthBox.Text, out var left);
        TryParseDouble(RightWidthBox.Text, out var right);
        TryParseInt(CounterStartBox.Text, out var counter);
        return new CrossAxisDrawParameters
        {
            Station = station,
            LeftWidth = left,
            RightWidth = right,
            AutoNaming = AutoNamingRadio.IsChecked == true,
            Prefix = PrefixBox.Text.Trim(),
            CounterStart = counter,
            IncreasingNumbers = IncreasingRadio.IsChecked == true,
            FixedName = FixedNameBox.Text.Trim()
        };
    }

    private static bool TryParseDouble(string? text, out double value) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
