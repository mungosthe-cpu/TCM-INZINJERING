using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TcmInzenjering.Plugin.Roads.Profile;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class ProfileTerrainDialog : Window
{
    private readonly double _crossAxisInterval;
    private bool _uiReady;

    public ProfileTerrainDialogResult? Result { get; private set; }

    public ProfileTerrainDialog(
        string axisName,
        double startStation,
        double endStation,
        double minTerrainElev,
        double maxTerrainElev,
        double crossAxisInterval,
        Window? owner = null)
    {
        _crossAxisInterval = crossAxisInterval > 1e-6 ? crossAxisInterval : 20.0;

        if (owner is not null)
        {
            Owner = owner;
        }

        InitializeComponent();

        TableNameBox.Text = $"PROFIL-1: {axisName}";
        ReloadTableTypes("TCM_1");

        foreach (var h in new[] { 500, 1000, 2000 })
        {
            HScaleBox.Items.Add(h.ToString(CultureInfo.InvariantCulture));
        }

        HScaleBox.SelectedItem = "1000";

        foreach (var v in new[] { 50, 100, 200 })
        {
            VScaleBox.Items.Add(v.ToString(CultureInfo.InvariantCulture));
        }

        VScaleBox.SelectedItem = "100";

        StartStationBox.Text = startStation.ToString("0.###", CultureInfo.InvariantCulture);
        EndStationBox.Text = endStation.ToString("0.###", CultureInfo.InvariantCulture);

        var baseElev = Math.Floor(minTerrainElev);
        var topElev = Math.Ceiling(maxTerrainElev + 1.0);
        BaseElevBox.Text = baseElev.ToString("0.###", CultureInfo.InvariantCulture);
        TopElevBox.Text = topElev.ToString("0.###", CultureInfo.InvariantCulture);
        MinElevHint.Text = $"Minimalna visina: {minTerrainElev:0.00}";
        MaxElevHint.Text = $"Maksimalna visina: {maxTerrainElev:0.00}";

        SourceInfo.Text =
            $"Osovina: {axisName}. Teren sa 3D projektovane nivelete (TCMPROJTER). " +
            $"Interval poprecnih osa: {_crossAxisInterval:0.##} m.";

        ModeBox.Items.Add(new ComboBoxItem { Content = "Fiksni interval", Tag = ProfileTabulationMode.FixedInterval });
        ModeBox.Items.Add(new ComboBoxItem
        {
            Content = $"Po poprecnim osama ({_crossAxisInterval:0.##} m)",
            Tag = ProfileTabulationMode.CrossAxes
        });
        ModeBox.Items.Add(new ComboBoxItem
        {
            Content = $"Po poprecnim osama + izmedju ({_crossAxisInterval:0.##} m obavezno)",
            Tag = ProfileTabulationMode.CrossAxesAndBetween
        });

        foreach (var item in new[] { ("Svaki 10 m", 10.0), ("Svaki 20 m", 20.0), ("Svaki 25 m", 25.0), ("Svaki 50 m", 50.0) })
        {
            IntervalBox.Items.Add(new ComboBoxItem { Content = item.Item1, Tag = item.Item2 });
        }

        IntervalBox.SelectedIndex = 1;
        FillBetweenOptions();

        ModeBox.SelectedIndex = 1;
        _uiReady = true;
        UpdateEnabled();
    }

    private void ReloadTableTypes(string? preferred)
    {
        var names = ProfileTableTypeCatalog.GetNames().ToList();
        if (names.Count == 0)
        {
            names.Add("TCM_1");
        }

        var previous = preferred
            ?? Convert.ToString(TableTypeBox.SelectedItem)
            ?? "TCM_1";

        TableTypeBox.Items.Clear();
        foreach (var name in names)
        {
            TableTypeBox.Items.Add(name);
        }

        var pick = names.FirstOrDefault(n =>
            string.Equals(n, previous, StringComparison.OrdinalIgnoreCase)) ?? names[0];
        TableTypeBox.SelectedItem = pick;
    }

    private void OnDefineTableType(object sender, RoutedEventArgs e)
    {
        var current = Convert.ToString(TableTypeBox.SelectedItem) ?? "TCM_1";
        var dialog = new ProfileTableTypeDialog(current, this);
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedTypeName))
        {
            ReloadTableTypes(dialog.SelectedTypeName);
        }
        else
        {
            ReloadTableTypes(current);
        }
    }

    private void FillBetweenOptions()
    {
        BetweenBox.Items.Clear();
        var cross = _crossAxisInterval;
        var divisors = new List<(string Label, int Div)>
        {
            ($"Polovina (svakih {cross / 2:0.##} m)", 2),
            ($"Cetvrtina (svakih {cross / 4:0.##} m)", 4)
        };
        if (Math.Abs(cross % 5) < 1e-6 || Math.Abs((cross / 5) - Math.Round(cross / 5)) < 1e-6)
        {
            var step = cross / 5.0;
            if (step >= 1.0 - 1e-6)
            {
                divisors.Insert(0, ($"Svakih {step:0.##} m (1/5)", 5));
            }
        }

        foreach (var (label, div) in divisors)
        {
            BetweenBox.Items.Add(new ComboBoxItem { Content = label, Tag = div });
        }

        BetweenBox.SelectedIndex = 0;
    }

    private void OnTabulateChanged(object sender, RoutedEventArgs e)
    {
        if (_uiReady)
        {
            UpdateEnabled();
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady)
        {
            UpdateEnabled();
        }
    }

    private void UpdateEnabled()
    {
        var on = TabulateCheck.IsChecked == true;
        ModeBox.IsEnabled = on;

        var mode = GetSelectedMode();
        var fixedMode = mode == ProfileTabulationMode.FixedInterval;
        var betweenMode = mode == ProfileTabulationMode.CrossAxesAndBetween;

        IntervalLabel.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        IntervalBox.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        IntervalBox.IsEnabled = on && fixedMode;

        BetweenLabel.Visibility = betweenMode ? Visibility.Visible : Visibility.Collapsed;
        BetweenBox.Visibility = betweenMode ? Visibility.Visible : Visibility.Collapsed;
        BetweenBox.IsEnabled = on && betweenMode;

        CrossHint.Text = mode switch
        {
            ProfileTabulationMode.CrossAxes =>
                $"Tabeliranje tacno na poprecnim osama ({_crossAxisInterval:0.##} m) — obavezno.",
            ProfileTabulationMode.CrossAxesAndBetween =>
                $"Poprecne ose na {_crossAxisInterval:0.##} m su uvek ukljucene; izaberite podelu izmedju njih.",
            _ => "Fiksni korak nezavisan od poprecnih osa."
        };
    }

    private ProfileTabulationMode GetSelectedMode()
    {
        if (ModeBox.SelectedItem is ComboBoxItem { Tag: ProfileTabulationMode mode })
        {
            return mode;
        }

        return ProfileTabulationMode.FixedInterval;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryParse(StartStationBox.Text, out var start) ||
            !TryParse(EndStationBox.Text, out var end) ||
            end <= start)
        {
            MessageBox.Show(this, "Unesite ispravan opseg stacionaze (Od < Do).", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParse(BaseElevBox.Text, out var baseElev) ||
            !TryParse(TopElevBox.Text, out var topElev) ||
            topElev <= baseElev)
        {
            MessageBox.Show(this, "Referentna visina mora biti manja od visine vrha profila.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(Convert.ToString(HScaleBox.SelectedItem), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(Convert.ToString(VScaleBox.SelectedItem), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v))
        {
            MessageBox.Show(this, "Izaberite horizontalnu i vertikalnu razmeru.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var mode = GetSelectedMode();
        var interval = _crossAxisInterval;
        var betweenDivisor = 1;

        if (mode == ProfileTabulationMode.FixedInterval)
        {
            if (IntervalBox.SelectedItem is ComboBoxItem { Tag: double tag })
            {
                interval = tag;
            }
            else
            {
                interval = 25;
            }
        }
        else
        {
            interval = _crossAxisInterval;
        }

        if (mode == ProfileTabulationMode.CrossAxesAndBetween)
        {
            if (BetweenBox.SelectedItem is ComboBoxItem { Tag: int div } && div >= 1)
            {
                betweenDivisor = div;
            }
            else
            {
                betweenDivisor = 2;
            }
        }

        Result = new ProfileTerrainDialogResult
        {
            TableName = string.IsNullOrWhiteSpace(TableNameBox.Text)
                ? "PROFIL-1"
                : TableNameBox.Text.Trim(),
            TableType = Convert.ToString(TableTypeBox.SelectedItem) ?? "TCM_1",
            HorizontalDenom = h,
            VerticalDenom = v,
            StartStation = start,
            EndStation = end,
            BaseElevation = baseElev,
            TopElevation = topElev,
            StationInterval = interval,
            TabulationMode = mode,
            CrossAxisInterval = _crossAxisInterval,
            BetweenDivisor = betweenDivisor,
            DrawTabulation = TabulateCheck.IsChecked == true,
            DrawVerticals = VerticalsCheck.IsChecked == true
        };

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
