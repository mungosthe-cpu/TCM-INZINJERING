using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class Plo2TanDialog : Window
{
    private readonly double _axisLength;
    private bool _updating;
    private bool _isUiReady;

    public string AxisName { get; private set; } = "OS-1";
    public double CurveRadius { get; private set; } = 50;
    public double StartStation { get; private set; }
    public double EndStation { get; private set; }
    public double Interval { get; private set; } = 20;
    public double TextHeight { get; private set; } = 2.5;
    public double TickLength { get; private set; } = 2.0;
    public string Prefix { get; private set; } = "STA ";
    public StationLabelOptions StationOptions { get; private set; } = new();

    public Plo2TanDialog(double axisLength, double suggestedStartStation = 0)
    {
        InitializeComponent();
        _isUiReady = true;
        _axisLength = Math.Max(axisLength, 0);

        AxisNameBox.Text = "OS-1";
        RadiusBox.Text = "50";
        StartStationBox.Text = suggestedStartStation.ToString("0.####", CultureInfo.InvariantCulture);
        EndStationBox.Text = (suggestedStartStation + _axisLength).ToString("0.####", CultureInfo.InvariantCulture);
        IntervalBox.Text = "20";
        PrefixBox.Text = "STA ";
        TextHeightBox.Text = "2.5";
        TickLengthBox.Text = "2.0";
        LengthInfo.Text = $"Dužina osovine (približno): {_axisLength:F2} m. Krajnja stacionaža = početna + dužina.";

        UpdateWholeIntervalCaption();
        UpdateNestedEnabledState();
    }

    private void OnEqualIntervalChanged(object sender, RoutedEventArgs e) => UpdateNestedEnabledState();

    private void OnWholeIntervalChanged(object sender, RoutedEventArgs e) => UpdateNestedEnabledState();

    private void UpdateNestedEnabledState()
    {
        if (!_isUiReady || IntervalBoundsPanel is null || WholeIntervalCheck is null)
        {
            return;
        }

        var equalEnabled = EqualIntervalCheck.IsChecked == true;
        IntervalBoundsPanel.IsEnabled = equalEnabled;
        WholeIntervalCheck.IsEnabled = equalEnabled;

        var wholeEnabled = equalEnabled && WholeIntervalCheck.IsChecked == true;
        StartStationBox.IsEnabled = wholeEnabled;
        EndStationBox.IsEnabled = wholeEnabled;
        AlignPanel.IsEnabled = wholeEnabled;
    }

    private void OnStationChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            if (ReferenceEquals(sender, StartStationBox) &&
                TryParse(StartStationBox.Text, out var start))
            {
                EndStationBox.Text = (start + _axisLength).ToString("0.####", CultureInfo.InvariantCulture);
            }
            else if (ReferenceEquals(sender, EndStationBox) &&
                     TryParse(EndStationBox.Text, out var end))
            {
                StartStationBox.Text = (end - _axisLength).ToString("0.####", CultureInfo.InvariantCulture);
            }

            UpdateWholeIntervalCaption();
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateWholeIntervalCaption()
    {
        if (!_isUiReady || WholeIntervalCheck is null || StartStationBox is null || EndStationBox is null)
        {
            return;
        }

        if (TryParse(StartStationBox.Text, out var start) && TryParse(EndStationBox.Text, out var end))
        {
            WholeIntervalCheck.Content =
                $"Po celom intervalu ({start:0.###}, {end:0.###})";
        }
        else
        {
            WholeIntervalCheck.Content = "Po celom intervalu";
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AxisNameBox.Text))
        {
            MessageBox.Show(this, "Unesite ime osovine.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParse(RadiusBox.Text, out var radius) || radius <= 0)
        {
            MessageBox.Show(this, "Radijus mora biti broj veći od 0.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParse(StartStationBox.Text, out var start))
        {
            MessageBox.Show(this, "Početna stacionaža nije validan broj.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParse(EndStationBox.Text, out var end))
        {
            MessageBox.Show(this, "Krajnja stacionaža nije validan broj.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParse(IntervalBox.Text, out var interval) || interval <= 0)
        {
            MessageBox.Show(this, "Razdaljina između stacionaža mora biti broj veći od 0.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParse(TextHeightBox.Text, out var textHeight) || textHeight <= 0)
        {
            MessageBox.Show(this, "Visina teksta mora biti broj veći od 0.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParse(TickLengthBox.Text, out var tickLength) || tickLength <= 0)
        {
            MessageBox.Show(this, "Dužina linije stacionaže mora biti broj veći od 0.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AxisName = AxisNameBox.Text.Trim();
        CurveRadius = radius;
        StartStation = start;
        EndStation = end;
        Interval = interval;
        TextHeight = textHeight;
        TickLength = tickLength;
        Prefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? string.Empty : PrefixBox.Text;

        StationOptions = new StationLabelOptions
        {
            EqualIntervalInBounds = EqualIntervalCheck.IsChecked == true,
            WholeInterval = WholeIntervalCheck.IsChecked == true,
            StartStation = start,
            EndStation = end,
            AlignToStart = AlignStartRadio.IsChecked == true,
            LabelAtStart = AtStartCheck.IsChecked == true,
            LabelAtEnd = AtEndCheck.IsChecked == true,
            LabelAtMainPoints = AtMainPointsCheck.IsChecked == true,
            Interval = interval,
            Prefix = Prefix,
            TextHeight = textHeight,
            TickLength = TickLength
        };

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
