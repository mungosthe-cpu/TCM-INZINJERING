using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class Plo2TanDialog : Window
{
    private readonly double _axisLength;
    private readonly Plo2TanDialogState _state;
    private readonly HashSet<string> _existingAxisNames;
    private bool _updating;
    private bool _isUiReady;

    public Plo2TanDialogCloseAction CloseAction { get; private set; } = Plo2TanDialogCloseAction.Cancelled;

    public string AxisName => _state.AxisName;
    public double CurveRadius => _state.CurveRadius;
    public double StartStation => _state.StartStation;
    public double EndStation => _state.EndStation;
    public double Interval => _state.Interval;
    public double TextHeight => _state.TextHeight;
    public double TickLength => _state.TickLength;
    public string Prefix => _state.Prefix;
    public int AxisCounterStart => _state.AxisCounterStart;
    public StationLabelOptions StationOptions => _state.ToStationOptions();

    public Plo2TanDialog(
        double axisLength,
        Plo2TanDialogState state,
        IEnumerable<string>? existingAxisNames = null)
    {
        _axisLength = Math.Max(axisLength, 0);
        _state = state;
        _existingAxisNames = new HashSet<string>(
            existingAxisNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        _isUiReady = true;
        LoadFromState();
        UpdateWholeIntervalCaption();
        UpdateNestedEnabledState();
    }

    private void LoadFromState()
    {
        AxisNameBox.Text = _state.AxisName;
        RadiusBox.Text = _state.CurveRadius.ToString("0.####", CultureInfo.InvariantCulture);
        StartStationBox.Text = _state.StartStation.ToString("0.####", CultureInfo.InvariantCulture);
        EndStationBox.Text = _state.EndStation.ToString("0.####", CultureInfo.InvariantCulture);
        IntervalBox.Text = _state.Interval.ToString("0.####", CultureInfo.InvariantCulture);
        PrefixBox.Text = _state.Prefix;
        TextHeightBox.Text = _state.TextHeight.ToString("0.####", CultureInfo.InvariantCulture);
        TickLengthBox.Text = _state.TickLength.ToString("0.####", CultureInfo.InvariantCulture);
        AxisCounterStartBox.Text = _state.AxisCounterStart.ToString(CultureInfo.InvariantCulture);
        EqualIntervalCheck.IsChecked = _state.EqualIntervalInBounds;
        WholeIntervalCheck.IsChecked = _state.WholeInterval;
        AlignStartRadio.IsChecked = _state.AlignToStart;
        AlignEndRadio.IsChecked = !_state.AlignToStart;
        AtStartCheck.IsChecked = _state.LabelAtStart;
        AtEndCheck.IsChecked = _state.LabelAtEnd;
        AtMainPointsCheck.IsChecked = _state.LabelAtMainPoints;
        StationFormatBox.SelectedIndex = _state.LabelFormat == StationLabelFormat.ChainageOnly ? 1 : 0;
        ChainageFormatBox.Text = _state.ChainageFormat.ToString(CultureInfo.InvariantCulture);
        UpdateChainageFormatPreview();
        SegmentLabelsCheck.IsChecked = _state.DrawSegmentLabels;
        AciColorHelper.ApplyToButton(AxisColorButton, _state.AxisColorIndex);
        AciColorHelper.ApplyToButton(StationTextColorButton, _state.StationTextColorIndex);
        AciColorHelper.ApplyToButton(StationTickColorButton, _state.StationTickColorIndex);
        AciColorHelper.ApplyToButton(SegmentLabelColorButton, _state.SegmentLabelColorIndex);
        LengthInfo.Text =
            $"Dužina osovine (približno): {_axisLength:F2} m. Početna/krajnja stacionaža = rastojanje duž polilinije (0 – {_axisLength:F2}). Oznake: prefiks + brojač ({_state.AxisCounterStart}, {_state.AxisCounterStart + 1}, ...) + stacionaža ({ChainageFormatter.GetSampleLabel(_state.ChainageFormat).TrimStart('-')}).";
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
        PickStartStationButton.IsEnabled = wholeEnabled;
        PickEndStationButton.IsEnabled = wholeEnabled;
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
                start = Clamp(start, 0, _axisLength);
                StartStationBox.Text = start.ToString("0.####", CultureInfo.InvariantCulture);
                if (TryParse(EndStationBox.Text, out var end) && end < start)
                {
                    EndStationBox.Text = _axisLength.ToString("0.####", CultureInfo.InvariantCulture);
                }
            }
            else if (ReferenceEquals(sender, EndStationBox) &&
                     TryParse(EndStationBox.Text, out var endValue))
            {
                endValue = Clamp(endValue, 0, _axisLength);
                if (TryParse(StartStationBox.Text, out var startValue) && endValue < startValue)
                {
                    endValue = startValue;
                }

                EndStationBox.Text = endValue.ToString("0.####", CultureInfo.InvariantCulture);
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
        if (!TryReadInputs(out var message))
        {
            MessageBox.Show(this, message, "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseAction = Plo2TanDialogCloseAction.Confirmed;
        Plo2TanDialogPreferences.SaveFrom(_state);
        DialogResult = true;
    }

    private void OnPickStartStation(object sender, RoutedEventArgs e)
    {
        SaveInputsBestEffort();
        CloseAction = Plo2TanDialogCloseAction.PickStartStation;
        DialogResult = false;
    }

    private void OnPickEndStation(object sender, RoutedEventArgs e)
    {
        SaveInputsBestEffort();
        CloseAction = Plo2TanDialogCloseAction.PickEndStation;
        DialogResult = false;
    }

    private bool TryReadInputs(out string errorMessage)
    {
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(AxisNameBox.Text))
        {
            errorMessage = "Unesite ime osovine.";
            return false;
        }

        var axisName = AxisNameBox.Text.Trim();
        if (_existingAxisNames.Contains(axisName))
        {
            errorMessage =
                $"Osovina '{axisName}' već postoji u crtežu. " +
                "Unesite jedinstveno ime, na primer sledeće slobodno OSA-n.";
            AxisNameBox.Focus();
            AxisNameBox.SelectAll();
            return false;
        }

        if (!TryParse(RadiusBox.Text, out var radius) || radius <= 0)
        {
            errorMessage = "Radijus mora biti broj veći od 0.";
            return false;
        }

        if (!TryParse(StartStationBox.Text, out var start))
        {
            errorMessage = "Početna stacionaža nije validan broj.";
            return false;
        }

        if (!TryParse(EndStationBox.Text, out var end))
        {
            errorMessage = "Krajnja stacionaža nije validan broj.";
            return false;
        }

        start = Clamp(start, 0, _axisLength);
        end = Clamp(end, 0, _axisLength);
        if (end < start)
        {
            errorMessage = "Krajnja stacionaža mora biti veća ili jednaka početnoj.";
            return false;
        }

        if (!TryParse(IntervalBox.Text, out var interval) || interval <= 0)
        {
            errorMessage = "Razdaljina između stacionaža mora biti broj veći od 0.";
            return false;
        }

        if (!TryParse(TextHeightBox.Text, out var textHeight) || textHeight <= 0)
        {
            errorMessage = "Visina teksta mora biti broj veći od 0.";
            return false;
        }

        if (!TryParse(TickLengthBox.Text, out var tickLength) || tickLength <= 0)
        {
            errorMessage = "Dužina linije stacionaže mora biti broj veći od 0.";
            return false;
        }

        if (!TryParseAxisCounter(AxisCounterStartBox.Text, out var axisCounterStart))
        {
            errorMessage = "Početna vrednost brojača osa mora biti ceo broj veći ili jednak 1.";
            return false;
        }

        _state.AxisName = axisName;
        _state.CurveRadius = radius;
        _state.StartStation = start;
        _state.EndStation = end;
        _state.Interval = interval;
        _state.TextHeight = textHeight;
        _state.TickLength = tickLength;
        _state.AxisCounterStart = axisCounterStart;
        _state.Prefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? string.Empty : PrefixBox.Text;
        _state.EqualIntervalInBounds = EqualIntervalCheck.IsChecked == true;
        _state.WholeInterval = WholeIntervalCheck.IsChecked == true;
        _state.AlignToStart = AlignStartRadio.IsChecked == true;
        _state.LabelAtStart = AtStartCheck.IsChecked == true;
        _state.LabelAtEnd = AtEndCheck.IsChecked == true;
        _state.LabelAtMainPoints = AtMainPointsCheck.IsChecked == true;
        _state.LabelFormat = StationFormatBox.SelectedIndex == 1
            ? StationLabelFormat.ChainageOnly
            : StationLabelFormat.ProjectCounter;
        if (!TryParseChainageFormat(ChainageFormatBox.Text, out var chainageFormat))
        {
            errorMessage = $"Format ispisa mora biti ceo broj od {ChainageFormatter.MinFormat} do {ChainageFormatter.MaxFormat}.";
            return false;
        }

        _state.ChainageFormat = chainageFormat;
        _state.DrawSegmentLabels = SegmentLabelsCheck.IsChecked == true;
        return true;
    }

    private void OnAxisColorClick(object sender, RoutedEventArgs e) =>
        PickColor(AxisColorButton, value => _state.AxisColorIndex = value);

    private void OnStationTextColorClick(object sender, RoutedEventArgs e) =>
        PickColor(StationTextColorButton, value => _state.StationTextColorIndex = value);

    private void OnStationTickColorClick(object sender, RoutedEventArgs e) =>
        PickColor(StationTickColorButton, value => _state.StationTickColorIndex = value);

    private void OnSegmentLabelColorClick(object sender, RoutedEventArgs e) =>
        PickColor(SegmentLabelColorButton, value => _state.SegmentLabelColorIndex = value);

    private void PickColor(System.Windows.Controls.Button button, Action<short> apply)
    {
        var current = button.Tag is short aci ? aci : DrawingColorDefaults.Axis;
        AciColorHelper.ShowPicker(button, current, selected =>
        {
            apply(selected);
            AciColorHelper.ApplyToButton(button, selected);
        });
    }

    private void SaveInputsBestEffort()
    {
        if (!string.IsNullOrWhiteSpace(AxisNameBox.Text))
        {
            _state.AxisName = AxisNameBox.Text.Trim();
        }

        if (TryParse(RadiusBox.Text, out var radius))
        {
            _state.CurveRadius = radius;
        }

        if (TryParse(StartStationBox.Text, out var start))
        {
            _state.StartStation = Clamp(start, 0, _axisLength);
        }

        if (TryParse(EndStationBox.Text, out var end))
        {
            _state.EndStation = Clamp(end, 0, _axisLength);
        }

        if (TryParse(IntervalBox.Text, out var interval))
        {
            _state.Interval = interval;
        }

        if (TryParse(TextHeightBox.Text, out var textHeight))
        {
            _state.TextHeight = textHeight;
        }

        if (TryParse(TickLengthBox.Text, out var tickLength))
        {
            _state.TickLength = tickLength;
        }

        if (TryParseAxisCounter(AxisCounterStartBox.Text, out var axisCounterStart))
        {
            _state.AxisCounterStart = axisCounterStart;
        }

        _state.Prefix = PrefixBox.Text;
        _state.EqualIntervalInBounds = EqualIntervalCheck.IsChecked == true;
        _state.WholeInterval = WholeIntervalCheck.IsChecked == true;
        _state.AlignToStart = AlignStartRadio.IsChecked == true;
        _state.LabelAtStart = AtStartCheck.IsChecked == true;
        _state.LabelAtEnd = AtEndCheck.IsChecked == true;
        _state.LabelAtMainPoints = AtMainPointsCheck.IsChecked == true;
        _state.LabelFormat = StationFormatBox.SelectedIndex == 1
            ? StationLabelFormat.ChainageOnly
            : StationLabelFormat.ProjectCounter;
        if (TryParseChainageFormat(ChainageFormatBox.Text, out var chainageFormat))
        {
            _state.ChainageFormat = chainageFormat;
        }

        _state.DrawSegmentLabels = SegmentLabelsCheck.IsChecked == true;
    }

    private void OnPickChainageFormat(object sender, RoutedEventArgs e)
    {
        var current = TryParseChainageFormat(ChainageFormatBox.Text, out var format)
            ? format
            : _state.ChainageFormat;
        var dialog = new ChainageFormatDialog(current, this);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _state.ChainageFormat = dialog.SelectedFormat;
        ChainageFormatBox.Text = dialog.SelectedFormat.ToString(CultureInfo.InvariantCulture);
        UpdateChainageFormatPreview();
    }

    private void OnChainageFormatChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !_isUiReady)
        {
            return;
        }

        if (TryParseChainageFormat(ChainageFormatBox.Text, out var format))
        {
            _state.ChainageFormat = format;
            UpdateChainageFormatPreview();
        }
    }

    private void UpdateChainageFormatPreview()
    {
        if (!_isUiReady || ChainageFormatPreview is null)
        {
            return;
        }

        ChainageFormatPreview.Text = ChainageFormatter.GetSampleLabel(_state.ChainageFormat);
    }

    private static bool TryParseChainageFormat(string text, out int format)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out format))
        {
            format = ChainageFormatter.DefaultFormat;
            return false;
        }

        if (format < ChainageFormatter.MinFormat || format > ChainageFormatter.MaxFormat)
        {
            format = ChainageFormatter.DefaultFormat;
            return false;
        }

        return true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseAction = Plo2TanDialogCloseAction.Cancelled;
        DialogResult = false;
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(max, value));

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseAxisCounter(string text, out int value)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 1)
        {
            value = 1;
            return false;
        }

        return true;
    }
}
