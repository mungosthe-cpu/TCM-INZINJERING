using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TcmInzenjering.Plugin.Roads.CrossAxis;

namespace TcmInzenjering.Plugin.Dialogs;

public enum CrossAxisSettingsCloseAction
{
    Cancelled,
    Confirmed,
    PickAxesInDrawing,
    PickLabelsOffset,
    PickStationsOffset
}

public sealed class CrossAxisSettingsDialogResult
{
    public CrossAxisPlacementSettings Settings { get; init; } = new();
    public bool IndividualMode { get; init; }
    public IReadOnlyList<long> SelectedHandles { get; init; } = Array.Empty<long>();
    public IReadOnlyList<CrossAxisInfo> AllAxes { get; init; } = Array.Empty<CrossAxisInfo>();
}

public partial class CrossAxisSettingsDialog : Window
{
    private readonly Func<IReadOnlyList<CrossAxisInfo>> _reloadAxes;
    private readonly Func<long, CrossAxisPlacementSettings> _loadSettings;
    private IReadOnlyList<CrossAxisInfo> _axes;
    private IReadOnlyList<long>? _pendingSelection;
    private bool _loading;

    public CrossAxisSettingsCloseAction CloseAction { get; private set; } = CrossAxisSettingsCloseAction.Cancelled;

    public CrossAxisSettingsDialogResult Result { get; private set; } = new();

    public CrossAxisSettingsDialog(
        IReadOnlyList<CrossAxisInfo> axes,
        Func<long, CrossAxisPlacementSettings> loadSettings,
        Func<IReadOnlyList<CrossAxisInfo>> reloadAxes,
        CrossAxisPlacementSettings? initialSettings = null,
        IReadOnlyList<long>? selectedHandles = null)
    {
        _axes = axes;
        _loadSettings = loadSettings;
        _reloadAxes = reloadAxes;
        _pendingSelection = selectedHandles;
        InitializeComponent();
        AxisListBox.ItemsSource = _axes;
        ApplySettingsToUi(initialSettings ?? new CrossAxisPlacementSettings());
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // Selektuj tek kad je ListBox layout-ovan (inače SelectAll ponekad ne radi).
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                ApplySelection(_pendingSelection, selectAllWhenEmpty: _pendingSelection is null);
                if (AxisListBox.SelectedItem is not null)
                {
                    AxisListBox.ScrollIntoView(AxisListBox.SelectedItem);
                }

                AxisListBox.Focus();
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ReloadAxes(IReadOnlyList<CrossAxisInfo> axes, IReadOnlyList<long>? selectedHandles = null)
    {
        _axes = axes;
        _pendingSelection = selectedHandles;
        AxisListBox.ItemsSource = null;
        AxisListBox.ItemsSource = _axes;
        ApplySelection(selectedHandles, selectAllWhenEmpty: selectedHandles is null);
    }

    public void ApplyPickedOffsets(bool forLabels, double offsetX, double offsetY)
    {
        if (forLabels)
        {
            LabelsOffsetXBox.Text = offsetX.ToString("0.###", CultureInfo.InvariantCulture);
            LabelsOffsetYBox.Text = offsetY.ToString("0.###", CultureInfo.InvariantCulture);
            return;
        }

        StationsOffsetXBox.Text = offsetX.ToString("0.###", CultureInfo.InvariantCulture);
        StationsOffsetYBox.Text = offsetY.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void ApplySelection(IReadOnlyList<long>? selectedHandles, bool selectAllWhenEmpty)
    {
        _loading = true;
        try
        {
            AxisListBox.UnselectAll();
            if (_axes.Count == 0)
            {
                return;
            }

            if (selectedHandles is null)
            {
                if (selectAllWhenEmpty)
                {
                    AxisListBox.SelectAll();
                    // Fallback ako SelectAll ne popuni SelectedItems (WPF quirk).
                    if (AxisListBox.SelectedItems.Count == 0 && _axes.Count > 0)
                    {
                        foreach (var axis in _axes)
                        {
                            AxisListBox.SelectedItems.Add(axis);
                        }
                    }
                }

                return;
            }

            if (selectedHandles.Count == 0)
            {
                return;
            }

            var wanted = new HashSet<long>(selectedHandles);
            foreach (var axis in _axes)
            {
                if (wanted.Contains(axis.Handle))
                {
                    AxisListBox.SelectedItems.Add(axis);
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnIndividualModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || AxisListBox is null)
        {
            return;
        }

        if (IndividualModeCheck.IsChecked == true && AxisListBox.SelectedItems.Count == 1 &&
            AxisListBox.SelectedItem is CrossAxisInfo axis)
        {
            ApplySettingsToUi(_loadSettings(axis.Handle));
        }
    }

    private void OnAxisSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IndividualModeCheck.IsChecked != true || AxisListBox.SelectedItems.Count != 1)
        {
            return;
        }

        if (AxisListBox.SelectedItem is CrossAxisInfo axis)
        {
            ApplySettingsToUi(_loadSettings(axis.Handle));
        }
    }

    private void OnClearSelection(object sender, RoutedEventArgs e) =>
        AxisListBox.UnselectAll();

    private void OnPickAxesInDrawing(object sender, RoutedEventArgs e)
    {
        if (!TryReadUi(out var settings, out var message))
        {
            MessageBox.Show(this, message, "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = BuildResult(settings);
        CloseAction = CrossAxisSettingsCloseAction.PickAxesInDrawing;
        DialogResult = false;
    }

    private void OnPickLabelsOffset(object sender, RoutedEventArgs e) =>
        BeginPickOffset(CrossAxisSettingsCloseAction.PickLabelsOffset);

    private void OnPickStationsOffset(object sender, RoutedEventArgs e) =>
        BeginPickOffset(CrossAxisSettingsCloseAction.PickStationsOffset);

    private void BeginPickOffset(CrossAxisSettingsCloseAction action)
    {
        if (GetSelectedHandles().Count == 0)
        {
            MessageBox.Show(this, "Izaberite bar jednu poprečnu osu u tabeli.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryReadUi(out var settings, out var message))
        {
            MessageBox.Show(this, message, "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = BuildResult(settings);
        CloseAction = action;
        DialogResult = false;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryReadUi(out var settings, out var message))
        {
            MessageBox.Show(this, message, "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (GetSelectedHandles().Count == 0)
        {
            MessageBox.Show(this, "Izaberite bar jednu poprečnu osu u tabeli.", "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = BuildResult(settings);
        CloseAction = CrossAxisSettingsCloseAction.Confirmed;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseAction = CrossAxisSettingsCloseAction.Cancelled;
        DialogResult = false;
    }

    private CrossAxisSettingsDialogResult BuildResult(CrossAxisPlacementSettings settings) =>
        new()
        {
            Settings = settings,
            IndividualMode = IndividualModeCheck.IsChecked == true,
            SelectedHandles = GetSelectedHandles(),
            AllAxes = _axes
        };

    private IReadOnlyList<long> GetSelectedHandles() =>
        AxisListBox.SelectedItems.Cast<CrossAxisInfo>().Select(axis => axis.Handle).ToList();

    private void ApplySettingsToUi(CrossAxisPlacementSettings settings)
    {
        _loading = true;
        try
        {
            LabelsEnabledCheck.IsChecked = settings.Labels.Enabled;
            LabelsLeftRadio.IsChecked = settings.Labels.Side == CrossAxisSide.Left;
            LabelsRightRadio.IsChecked = settings.Labels.Side == CrossAxisSide.Right;
            LabelsOffsetXBox.Text = settings.Labels.OffsetX.ToString("0.###", CultureInfo.InvariantCulture);
            LabelsOffsetYBox.Text = settings.Labels.OffsetY.ToString("0.###", CultureInfo.InvariantCulture);

            StationsEnabledCheck.IsChecked = settings.Stations.Enabled;
            StationsLeftRadio.IsChecked = settings.Stations.Side == CrossAxisSide.Left;
            StationsRightRadio.IsChecked = settings.Stations.Side == CrossAxisSide.Right;
            StationsOffsetXBox.Text = settings.Stations.OffsetX.ToString("0.###", CultureInfo.InvariantCulture);
            StationsOffsetYBox.Text = settings.Stations.OffsetY.ToString("0.###", CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
        }
    }

    private bool TryReadUi(out CrossAxisPlacementSettings settings, out string errorMessage)
    {
        settings = new CrossAxisPlacementSettings();
        errorMessage = string.Empty;

        if (!TryParseOffset(LabelsOffsetXBox.Text, out var labelsX) ||
            !TryParseOffset(LabelsOffsetYBox.Text, out var labelsY) ||
            !TryParseOffset(StationsOffsetXBox.Text, out var stationsX) ||
            !TryParseOffset(StationsOffsetYBox.Text, out var stationsY))
        {
            errorMessage = "Odmak mora biti validan broj.";
            return false;
        }

        settings = new CrossAxisPlacementSettings
        {
            Labels = new CrossAxisOffsetSettings
            {
                Enabled = LabelsEnabledCheck.IsChecked == true,
                Side = LabelsRightRadio.IsChecked == true ? CrossAxisSide.Right : CrossAxisSide.Left,
                OffsetX = labelsX,
                OffsetY = labelsY
            },
            Stations = new CrossAxisOffsetSettings
            {
                Enabled = StationsEnabledCheck.IsChecked == true,
                Side = StationsRightRadio.IsChecked == true ? CrossAxisSide.Right : CrossAxisSide.Left,
                OffsetX = stationsX,
                OffsetY = stationsY
            }
        };
        return true;
    }

    private static bool TryParseOffset(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
