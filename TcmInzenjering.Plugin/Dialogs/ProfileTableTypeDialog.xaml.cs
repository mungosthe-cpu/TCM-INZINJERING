using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TcmInzenjering.Plugin.Roads.Profile;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class ProfileTableTypeDialog : Window
{
    private readonly ObservableCollection<ProfileTableType> _types = [];
    private readonly ObservableCollection<BandRow> _bands = [];
    private bool _loading;
    private ProfileTableType? _current;

    /// <summary>Tip koji treba da ostane selektovan u Unos terena.</summary>
    public string? SelectedTypeName { get; private set; }

    public ProfileTableTypeDialog(string? currentTypeName = null, Window? owner = null)
    {
        if (owner is not null)
        {
            Owner = owner;
        }

        InitializeComponent();
        ContentColumn.ItemsSource = ProfileBandContentLabels.All;
        ContentColumn.DisplayMemberPath = nameof(ProfileBandContentChoice.Label);
        ContentColumn.SelectedValuePath = nameof(ProfileBandContentChoice.Value);
        BandsGrid.ItemsSource = _bands;

        ReloadFromStore(currentTypeName);
    }

    private void ReloadFromStore(string? selectName)
    {
        _loading = true;
        _types.Clear();
        foreach (var t in ProfileTableTypeCatalog.GetAll())
        {
            _types.Add(t);
        }

        TypeList.ItemsSource = null;
        TypeList.ItemsSource = _types.Select(t => t.Name).ToList();

        var pick = selectName;
        if (string.IsNullOrWhiteSpace(pick) ||
            !_types.Any(t => string.Equals(t.Name, pick, StringComparison.OrdinalIgnoreCase)))
        {
            pick = _types.FirstOrDefault()?.Name;
        }

        if (pick is not null)
        {
            TypeList.SelectedItem = _types
                .First(t => string.Equals(t.Name, pick, StringComparison.OrdinalIgnoreCase))
                .Name;
        }

        _loading = false;
        LoadSelected();
    }

    private void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LoadSelected();
    }

    private void LoadSelected()
    {
        if (TypeList.SelectedItem is not string name)
        {
            _current = null;
            return;
        }

        _current = _types.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Clone();
        if (_current is null)
        {
            return;
        }

        _loading = true;
        NameBox.Text = _current.Name;
        LabelWidthBox.Text = _current.LabelColumnWidth.ToString("0.###", CultureInfo.InvariantCulture);
        DefaultHeightBox.Text = _current.DefaultBandHeight.ToString("0.###", CultureInfo.InvariantCulture);
        _bands.Clear();
        foreach (var b in _current.Bands)
        {
            _bands.Add(BandRow.FromBand(b));
        }

        _loading = false;
    }

    private bool TryReadEditor(out ProfileTableType type)
    {
        type = null!;
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Unesite naziv tipa tabele.", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryParse(LabelWidthBox.Text, out var labelW) || labelW < 5)
        {
            MessageBox.Show(this, "Sirina kolone naziva mora biti ≥ 5.", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryParse(DefaultHeightBox.Text, out var defH) || defH < 1)
        {
            MessageBox.Show(this, "Podrazumevana visina rubrike mora biti ≥ 1.", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_bands.Count == 0)
        {
            MessageBox.Show(this, "Dodajte bar jednu rubriku.", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        BandsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        type = new ProfileTableType
        {
            Name = name,
            LabelColumnWidth = labelW,
            DefaultBandHeight = defH,
            Bands = _bands.Select((b, i) => b.ToBand(i)).ToList()
        };
        return true;
    }

    private void PersistCurrentList(ProfileTableType edited)
    {
        var list = _types.Select(t => t.Clone()).ToList();
        var oldName = _current?.Name;
        var idx = -1;
        if (!string.IsNullOrWhiteSpace(oldName))
        {
            idx = list.FindIndex(t =>
                string.Equals(t.Name, oldName, StringComparison.OrdinalIgnoreCase));
        }

        if (idx < 0)
        {
            idx = list.FindIndex(t =>
                string.Equals(t.Name, edited.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (idx >= 0)
        {
            if (string.Equals(list[idx].Name, "TCM_1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(edited.Name, "TCM_1", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Ne mozete preimenovati ugradjeni tip TCM_1.", "TCM-INŽINJERING",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            list[idx] = edited;
        }
        else
        {
            list.Add(edited);
        }

        if (list.All(t => !string.Equals(t.Name, "TCM_1", StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, ProfileTableType.CreateDefaultTcm1());
        }

        ProfileTableTypeCatalog.SaveAll(list);
        SelectedTypeName = edited.Name;
        ReloadFromStore(edited.Name);
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var baseName = "TCM_NOVI";
        var n = 1;
        while (_types.Any(t => string.Equals(t.Name, $"{baseName}_{n}", StringComparison.OrdinalIgnoreCase)))
        {
            n++;
        }

        var created = ProfileTableType.CreateDefaultTcm1();
        created.Name = $"{baseName}_{n}";
        _types.Add(created);
        ProfileTableTypeCatalog.Upsert(created);
        ReloadFromStore(created.Name);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (!TryReadEditor(out var src))
        {
            return;
        }

        var baseName = src.Name + "_kopija";
        var name = baseName;
        var n = 2;
        while (_types.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName}{n++}";
        }

        src.Name = name;
        ProfileTableTypeCatalog.Upsert(src);
        SelectedTypeName = name;
        ReloadFromStore(name);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (TypeList.SelectedItem is not string name)
        {
            return;
        }

        if (string.Equals(name, "TCM_1", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Ugradjeni tip TCM_1 ne moze da se obrise.", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Obrisati tip '{name}'?", "TCM-INŽINJERING",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        ProfileTableTypeCatalog.Delete(name);
        ReloadFromStore("TCM_1");
    }

    private void OnAddBand(object sender, RoutedEventArgs e)
    {
        if (!TryParse(DefaultHeightBox.Text, out var h) || h < 1)
        {
            h = 10;
        }

        _bands.Add(new BandRow
        {
            Title = $"RUBRIKA {_bands.Count + 1}",
            Content = ProfileBandContent.Blank,
            Height = h,
            TextAci = 7
        });
    }

    private void OnRemoveBand(object sender, RoutedEventArgs e)
    {
        if (BandsGrid.SelectedItem is BandRow row)
        {
            _bands.Remove(row);
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (BandsGrid.SelectedItem is not BandRow row)
        {
            return;
        }

        var idx = _bands.IndexOf(row);
        var newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= _bands.Count)
        {
            return;
        }

        _bands.Move(idx, newIdx);
        BandsGrid.SelectedItem = row;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryReadEditor(out var type))
        {
            return;
        }

        PersistCurrentList(type);
        MessageBox.Show(this, $"Tip '{type.Name}' je snimljen.", "TCM-INŽINJERING",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!TryReadEditor(out var type))
        {
            return;
        }

        PersistCurrentList(type);
        SelectedTypeName = type.Name;
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (TypeList.SelectedItem is string name)
        {
            SelectedTypeName = name;
        }

        DialogResult = true;
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private sealed class BandRow : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private ProfileBandContent _content = ProfileBandContent.Blank;
        private double _height = 10;
        private short _textAci = 7;

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public ProfileBandContent Content
        {
            get => _content;
            set { _content = value; OnPropertyChanged(); }
        }

        public double Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public short TextAci
        {
            get => _textAci;
            set { _textAci = value; OnPropertyChanged(); }
        }

        public static BandRow FromBand(ProfileTableBand b) =>
            new()
            {
                Title = b.Title,
                Content = b.Content,
                Height = b.Height,
                TextAci = b.TextAci
            };

        public ProfileTableBand ToBand(int index) =>
            new()
            {
                Code = $"LK_{index + 1}",
                Title = string.IsNullOrWhiteSpace(Title) ? $"LK_{index + 1}" : Title.Trim(),
                Content = Content,
                Height = Math.Max(1.0, Height),
                TextAci = TextAci < 1 ? (short)7 : TextAci
            };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
