using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.AutoCAD.Geometry;
using Microsoft.Win32;
using TcmInzenjering.Plugin.Roads;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Dialogs;

/// <summary>Jedna tačka u uređivaču terena (opciono vezana za DBPoint u crtežu).</summary>
public sealed class TerrainPointVm : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private double _x;
    private double _y;
    private double _z;
    private bool _isMarkedZZero;
    private bool _isMarkedZNegative;
    private bool _isMarkedDeviation;

    public TerrainPointVm(double x, double y, double z, long pointHandle = 0, bool isAdded = false)
    {
        _x = x;
        _y = y;
        _z = z;
        PointHandle = pointHandle;
        _isAdded = isAdded;
    }

    public long PointHandle { get; set; }

    private bool _isAdded;

    /// <summary>True ako je tacka dodata u ovom dijalogu (Dodaj / Ucitaj).</summary>
    public bool IsAdded
    {
        get => _isAdded;
        set
        {
            if (_isAdded == value)
            {
                return;
            }

            _isAdded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsMarkedZZero
    {
        get => _isMarkedZZero;
        set
        {
            if (_isMarkedZZero == value)
            {
                return;
            }

            _isMarkedZZero = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsMarkedZNegative
    {
        get => _isMarkedZNegative;
        set
        {
            if (_isMarkedZNegative == value)
            {
                return;
            }

            _isMarkedZNegative = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsMarkedDeviation
    {
        get => _isMarkedDeviation;
        set
        {
            if (_isMarkedDeviation == value)
            {
                return;
            }

            _isMarkedDeviation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText
    {
        get
        {
            var parts = new List<string>();
            if (IsAdded)
            {
                parts.Add("DODATA");
            }

            if (IsMarkedZZero)
            {
                parts.Add("Z=0");
            }

            if (IsMarkedZNegative)
            {
                parts.Add("Z<0");
            }

            if (IsMarkedDeviation)
            {
                parts.Add("ΔZ");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }
    }

    private int _displayIndex;

    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (_displayIndex == value)
            {
                return;
            }

            _displayIndex = value;
            OnPropertyChanged();
        }
    }

    public double X
    {
        get => _x;
        set
        {
            _x = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(XText));
        }
    }

    public double Y
    {
        get => _y;
        set
        {
            _y = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(YText));
        }
    }

    public double Z
    {
        get => _z;
        set
        {
            _z = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZText));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string XText
    {
        get => _x.ToString("0.000", Inv);
        set
        {
            if (TryParse(value, out var v))
            {
                X = v;
            }
        }
    }

    public string YText
    {
        get => _y.ToString("0.000", Inv);
        set
        {
            if (TryParse(value, out var v))
            {
                Y = v;
            }
        }
    }

    public string ZText
    {
        get => _z.ToString("0.000", Inv);
        set
        {
            if (TryParse(value, out var v))
            {
                Z = v;
            }
        }
    }

    public Point3d ToPoint3d() => new(X, Y, Z);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float, Inv, out value);
}

public partial class TerrainPointsSummaryDialog : Window
{
    private readonly ObservableCollection<TerrainPointVm> _points;
    private readonly HashSet<long> _pendingEraseHandles = [];
    private readonly Func<IReadOnlyList<TerrainPointVm>, IReadOnlyCollection<long>, string?> _applyToDrawing;
    private readonly Func<IReadOnlyList<TerrainPointVm>, IReadOnlyCollection<long>, string, string?> _buildTerrain;
    private readonly Func<string, IReadOnlyList<TerrainPointVm>, string?> _savePointsToProject;
    private readonly Func<Point3d?> _pickPoint;
    private readonly Func<long?> _pickDrawingPoint;
    private readonly Func<(IReadOnlyList<string> Names, string? Active, string Suggested)> _listNamedTerrains;
    private readonly Func<string, (IReadOnlyList<TerrainPointVm>? Points, string? Error)> _loadNamedTerrain;
    private readonly bool _appendMode;

    public TerrainPointsSummaryDialog(
        IReadOnlyList<TerrainPointVm> points,
        bool appendMode,
        bool rebuiltTin,
        Func<IReadOnlyList<TerrainPointVm>, IReadOnlyCollection<long>, string?> applyToDrawing,
        Func<IReadOnlyList<TerrainPointVm>, IReadOnlyCollection<long>, string, string?> buildTerrain,
        Func<string, IReadOnlyList<TerrainPointVm>, string?> savePointsToProject,
        Func<Point3d?> pickPoint,
        Func<long?> pickDrawingPoint,
        Func<(IReadOnlyList<string> Names, string? Active, string Suggested)> listNamedTerrains,
        Func<string, (IReadOnlyList<TerrainPointVm>? Points, string? Error)> loadNamedTerrain,
        string? statusHint = null,
        Window? owner = null)
    {
        _appendMode = appendMode;
        _applyToDrawing = applyToDrawing;
        _buildTerrain = buildTerrain;
        _savePointsToProject = savePointsToProject;
        _pickPoint = pickPoint;
        _pickDrawingPoint = pickDrawingPoint;
        _listNamedTerrains = listNamedTerrains;
        _loadNamedTerrain = loadNamedTerrain;

        if (owner is not null)
        {
            Owner = owner;
        }

        InitializeComponent();
        RefreshProjectFolderLabel();
        RefreshNamedTerrainList();

        _points = new ObservableCollection<TerrainPointVm>(points.Select(CloneVm));
        Renumber();
        PointsGrid.ItemsSource = _points;
        RefreshSummary(rebuiltTin);
        ApplyPointFilters();
        if (!string.IsNullOrWhiteSpace(statusHint))
        {
            HintBanner.Text = statusHint;
            HintBanner.Visibility = Visibility.Visible;
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyPointFilters();

    private void OnFilterDevTextChanged(object sender, TextChangedEventArgs e)
    {
        // Live update dok je filter odstupanja uključen (i dok se dijalog već učitao).
        if (FilterDevBox?.IsChecked == true)
        {
            ApplyPointFilters();
        }
    }

    private void ApplyPointFilters()
    {
        if (_points is null || PointsGrid is null)
        {
            return;
        }

        var filterZ0 = FilterZZeroBox?.IsChecked == true;
        var filterZNeg = FilterZNegBox?.IsChecked == true;
        var filterDev = FilterDevBox?.IsChecked == true;
        var threshold = 5.0;
        if (FilterDevValueBox is not null)
        {
            var raw = (FilterDevValueBox.Text ?? "5").Trim().Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold) ||
                threshold < 0)
            {
                threshold = 5.0;
            }
        }

        var avgZ = _points.Count > 0 ? _points.Average(p => p.Z) : 0.0;
        var anyFilter = filterZ0 || filterZNeg || filterDev;
        var matchCount = 0;

        foreach (var p in _points)
        {
            p.IsMarkedZZero = filterZ0 && Math.Abs(p.Z) <= 1e-9;
            p.IsMarkedZNegative = filterZNeg && p.Z < -1e-12;
            p.IsMarkedDeviation = filterDev && Math.Abs(p.Z - avgZ) > threshold + 1e-12;

            if (!anyFilter || p.IsMarkedZZero || p.IsMarkedZNegative || p.IsMarkedDeviation)
            {
                if (anyFilter && (p.IsMarkedZZero || p.IsMarkedZNegative || p.IsMarkedDeviation))
                {
                    matchCount++;
                }
            }
        }

        var view = CollectionViewSource.GetDefaultView(_points);
        if (view is not null)
        {
            if (!anyFilter)
            {
                view.Filter = null;
                FilterInfoText.Text = string.Empty;
            }
            else
            {
                view.Filter = obj =>
                {
                    if (obj is not TerrainPointVm p)
                    {
                        return false;
                    }

                    return p.IsMarkedZZero || p.IsMarkedZNegative || p.IsMarkedDeviation;
                };
                FilterInfoText.Text =
                    $"prosek Z={avgZ:0.000} m · prikazano {matchCount} / {_points.Count}";
            }

            view.Refresh();
        }

        RefreshSummary();
    }

    private void RefreshNamedTerrainList()
    {
        var (names, active, suggested) = _listNamedTerrains();
        NamedTerrainBox.Items.Clear();
        foreach (var name in names)
        {
            NamedTerrainBox.Items.Add(name);
        }

        NamedTerrainBox.Text = !string.IsNullOrWhiteSpace(active) ? active! : suggested;
    }

    private void RefreshProjectFolderLabel()
    {
        ProjectFolderText.Text = string.IsNullOrWhiteSpace(ProjectFolderPreferences.FolderPath)
            ? "(nije izabran folder projekta)"
            : ProjectFolderPreferences.FolderPath;
        ProjectFolderText.ToolTip = ProjectFolderText.Text;
    }

    private static TerrainPointVm CloneVm(TerrainPointVm p) =>
        new(p.X, p.Y, p.Z, p.PointHandle, p.IsAdded);

    private void Renumber()
    {
        for (var i = 0; i < _points.Count; i++)
        {
            _points[i].DisplayIndex = i + 1;
        }
    }

    private void RefreshSummary(bool? tinReady = null)
    {
        SummaryText.Text = _appendMode
            ? $"Ukupno tacaka u skupu: {_points.Count}. (rad sa skupom)"
            : $"Ukupno tacaka u skupu: {_points.Count}.";

        if (_points.Count == 0)
        {
            DetailText.Text =
                "Nema tacaka. Dodajte / ucitajte tacke, ili „Ucitaj teren“ za postojece ime, zatim „3DFACE teren“.";
            return;
        }

        var minZ = _points.Min(p => p.Z);
        var maxZ = _points.Max(p => p.Z);
        var tin = tinReady switch
        {
            true => "3DFACE teren je spreman (sacuvan pod imenom).",
            false => "Kliknite „3DFACE teren“ — unesite ime i napravite TIN.",
            _ => "Zelena / DODATA = nova tacka. ✎ = uredjivo X/Y/Z."
        };
        DetailText.Text = $"Z opseg: {minZ:0.000} … {maxZ:0.000}  |  {tin}";
    }

    private void CommitGrid()
    {
        try
        {
            PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            PointsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch (InvalidOperationException)
        {
            // DataGrid nije u edit režimu.
        }
    }

    private bool TryApply(out string? error)
    {
        CommitGrid();
        try
        {
            error = _applyToDrawing(_points.ToList(), _pendingEraseHandles);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is null)
        {
            _pendingEraseHandles.Clear();
        }

        return error is null;
    }

    private void SafeClose(bool accepted)
    {
        try
        {
            DialogResult = accepted;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnPickProjectFolder(object sender, RoutedEventArgs e)
    {
        if (ProjectFolderPreferences.TryPickFolder(this, out _))
        {
            RefreshProjectFolderLabel();
        }
    }

    private void OnAddPoint(object sender, RoutedEventArgs e)
    {
        CommitGrid();
        var previous = Visibility;
        Visibility = Visibility.Hidden;
        try
        {
            var picked = _pickPoint();
            if (picked is null)
            {
                return;
            }

            _points.Add(new TerrainPointVm(picked.Value.X, picked.Value.Y, picked.Value.Z, isAdded: true));
            Renumber();
            if (!TryApply(out var err))
            {
                MessageBox.Show(this, err ?? "Greska pri snimanju.", "TCM-ROADS",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ApplyPointFilters();
            PointsGrid.ScrollIntoView(_points[^1]);
            PointsGrid.SelectedItem = _points[^1];
        }
        finally
        {
            Visibility = previous;
            Activate();
        }
    }

    private void OnRemovePoint(object sender, RoutedEventArgs e)
    {
        CommitGrid();
        var selected = PointsGrid.SelectedItems.OfType<TerrainPointVm>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Izaberite jednu ili vise tacaka u tabeli (Ctrl+klik).", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var row in selected)
        {
            if (row.PointHandle != 0)
            {
                _pendingEraseHandles.Add(row.PointHandle);
            }

            _points.Remove(row);
        }

        Renumber();
        if (!TryApply(out var err))
        {
            MessageBox.Show(this, err ?? "Greska pri snimanju.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyPointFilters();
    }

    private void OnBuildTerrain(object sender, RoutedEventArgs e)
    {
        if (!TryApply(out var err))
        {
            MessageBox.Show(this, err ?? "Greska pri snimanju.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_points.Count < 3)
        {
            HintBanner.Text =
                "Za 3DFACE teren su potrebne najmanje 3 tacke. Dodajte ili ucitajte tacke, pa pokušajte ponovo.";
            HintBanner.Visibility = Visibility.Visible;
            MessageBox.Show(this, HintBanner.Text, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (names, active, suggested) = _listNamedTerrains();
        var prefill = !string.IsNullOrWhiteSpace(NamedTerrainBox.Text)
            ? NamedTerrainBox.Text.Trim()
            : (!string.IsNullOrWhiteSpace(active) ? active! : suggested);
        var nameDlg = new NamedTerrainDialog(names, prefill, owner: this);
        if (nameDlg.ShowDialog() != true)
        {
            return;
        }

        var terrainName = nameDlg.TerrainName;
        NamedTerrainBox.Text = terrainName;

        var buildErr = _buildTerrain(_points.ToList(), _pendingEraseHandles, terrainName);
        if (buildErr is not null)
        {
            MessageBox.Show(this, buildErr, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSummary(tinReady: false);
            return;
        }

        _pendingEraseHandles.Clear();
        HintBanner.Visibility = Visibility.Collapsed;
        RefreshNamedTerrainList();
        NamedTerrainBox.Text = terrainName;
        RefreshSummary(tinReady: true);
        MessageBox.Show(this,
            $"3DFACE teren „{terrainName}“ je napravljen / osvežen.\n" +
            "Kasnije: izaberite ime → Ucitaj teren, ili ponovo 3DFACE za izmene.",
            "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnLoadNamedTerrain(object sender, RoutedEventArgs e)
    {
        var name = (NamedTerrainBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Izaberite ili unesite ime terena.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (loaded, error) = _loadNamedTerrain(name);
        if (error is not null || loaded is null)
        {
            MessageBox.Show(this, error ?? "Teren nije pronadjen.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_points.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"Ucitati teren „{name}“ ({loaded.Count} tacaka)?\nTrenutni skup ({_points.Count}) ce biti zamenjen.",
                "TCM-ROADS", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        foreach (var row in _points.Where(p => p.PointHandle != 0))
        {
            _pendingEraseHandles.Add(row.PointHandle);
        }

        _points.Clear();
        foreach (var p in loaded)
        {
            _points.Add(CloneVm(p));
        }

        Renumber();
        if (!TryApply(out var err))
        {
            MessageBox.Show(this, err ?? "Greska pri snimanju u crtez.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HintBanner.Visibility = Visibility.Collapsed;
        ApplyPointFilters();
        MessageBox.Show(this,
            $"Ucitane tacke terena „{name}“.\nKliknite „3DFACE teren“ da osvezite TIN.",
            "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSavePoints(object sender, RoutedEventArgs e)
    {
        CommitGrid();
        if (_points.Count == 0)
        {
            MessageBox.Show(this, "Nema tacaka za snimanje.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pointSetName = (NamedTerrainBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pointSetName))
        {
            MessageBox.Show(this,
                "Unesite naziv skupa tacaka (polje „Imenovani teren“) pre snimanja u projekat.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryApply(out var err))
        {
            MessageBox.Show(this, err ?? "Greska pri snimanju.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = _savePointsToProject(pointSetName, _points.ToList());
        if (result is null)
        {
            MessageBox.Show(this, "Nema tacaka za snimanje.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.StartsWith("ERR:", StringComparison.Ordinal))
        {
            MessageBox.Show(this, result[4..].Trim(), "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshNamedTerrainList();
        NamedTerrainBox.Text = pointSetName;
        MessageBox.Show(this, result, "TCM-ROADS",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnPickFromDrawing(object sender, RoutedEventArgs e)
    {
        CommitGrid();
        Visibility = Visibility.Hidden;
        long? handle = null;
        try
        {
            handle = _pickDrawingPoint();
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

        if (handle is null || handle.Value == 0)
        {
            return;
        }

        var row = _points.FirstOrDefault(p => p.PointHandle == handle.Value);
        if (row is null)
        {
            MessageBox.Show(this,
                "Izabrana POINT tacka nije u trenutnom skupu terena.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Ako aktivni filter skriva red — privremeno ugasi filtere.
        if (FilterZZeroBox?.IsChecked == true ||
            FilterZNegBox?.IsChecked == true ||
            FilterDevBox?.IsChecked == true)
        {
            if (FilterZZeroBox is not null)
            {
                FilterZZeroBox.IsChecked = false;
            }

            if (FilterZNegBox is not null)
            {
                FilterZNegBox.IsChecked = false;
            }

            if (FilterDevBox is not null)
            {
                FilterDevBox.IsChecked = false;
            }

            ApplyPointFilters();
        }

        PointsGrid.SelectedItem = null;
        PointsGrid.SelectedItem = row;
        PointsGrid.UpdateLayout();
        PointsGrid.ScrollIntoView(row);
        PointsGrid.Focus();
    }

    private void OnLoadPoints(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Ucitaj tacke terena",
            Filter = TerrainPointFile.FileFilter,
            CheckFileExists = true
        };

        var folder = ProjectFolderPreferences.FolderPath;
        if (Directory.Exists(folder))
        {
            dlg.InitialDirectory = folder;
        }

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        List<Point3d> loaded;
        try
        {
            loaded = TerrainPointFile.Read(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Ne mogu da ucitam fajl:\n{ex.Message}", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (loaded.Count == 0)
        {
            MessageBox.Show(this, "Fajl ne sadrzi validne X,Y,Z tacke.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_points.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"Ucitano {loaded.Count} tacaka.\nZameniti trenutni skup ({_points.Count})?",
                "TCM-ROADS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                foreach (var row in _points.Where(p => p.PointHandle != 0))
                {
                    _pendingEraseHandles.Add(row.PointHandle);
                }

                _points.Clear();
            }
        }

        foreach (var p in loaded)
        {
            var dup = _points.FirstOrDefault(q =>
                Math.Abs(q.X - p.X) <= 1e-8 && Math.Abs(q.Y - p.Y) <= 1e-8);
            if (dup is null)
            {
                _points.Add(new TerrainPointVm(p.X, p.Y, p.Z, isAdded: true));
            }
            else
            {
                dup.Z = p.Z;
                dup.IsAdded = true;
            }
        }

        Renumber();
        if (!TryApply(out var err))
        {
            MessageBox.Show(this, err ?? "Greska pri snimanju u crtez.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyPointFilters();
        MessageBox.Show(this,
            $"Ucitano u crtez: {_points.Count} tacaka.\nZatim „3DFACE teren“ za TIN.",
            "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!TryApply(out var err))
        {
            MessageBox.Show(this, err ?? "Greska pri snimanju.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshSummary(tinReady: false);
        MessageBox.Show(this, "Koordinate su primenjene na crtez i sacuvane.", "TCM-ROADS",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryApply(out var err))
            {
                MessageBox.Show(this, err ?? "Greska pri snimanju.", "TCM-ROADS",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SafeClose(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseDialog(object sender, RoutedEventArgs e)
    {
        // IsCancel sam po sebi ne zatvara prozor kad DataGrid ostane u edit
        // režimu ili kad fokus "proguta" ESC/klik — zato eksplicitno zatvaramo.
        CommitGrid();
        SafeClose(false);
    }

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // Binding XText/YText/ZText handles parse on LostFocus.
    }
}
