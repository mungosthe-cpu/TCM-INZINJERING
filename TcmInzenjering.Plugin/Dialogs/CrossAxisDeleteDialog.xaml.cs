using System.Collections.ObjectModel;
using System.Windows;
using TcmInzenjering.Plugin.Roads.CrossAxis;

namespace TcmInzenjering.Plugin.Dialogs;

public enum CrossAxisDeleteCloseAction
{
    Closed,
    DeleteSelected,
    PickInDrawing,
    Refresh
}

public partial class CrossAxisDeleteDialog : Window
{
    private readonly ObservableCollection<CrossAxisInfo> _axes = new();

    public CrossAxisDeleteCloseAction CloseAction { get; private set; } = CrossAxisDeleteCloseAction.Closed;

    public IReadOnlyList<long> SelectedHandles { get; private set; } = Array.Empty<long>();

    public CrossAxisDeleteDialog(IReadOnlyList<CrossAxisInfo> axes)
    {
        InitializeComponent();
        AxesGrid.ItemsSource = _axes;
        Reload(axes);
    }

    public void Reload(IReadOnlyList<CrossAxisInfo> axes)
    {
        _axes.Clear();
        foreach (var axis in axes)
        {
            _axes.Add(axis);
        }
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        var handles = AxesGrid.SelectedItems
            .OfType<CrossAxisInfo>()
            .Select(a => a.Handle)
            .Distinct()
            .ToList();

        if (handles.Count == 0)
        {
            MessageBox.Show(
                this,
                "Izaberite jednu ili više poprečnih osa u tabeli.",
                "TCM-INŽINJERING",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Obrisati {handles.Count} poprečn{(handles.Count == 1 ? "u osu" : "ih osa")}?",
            "TCM-INŽINJERING",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        SelectedHandles = handles;
        CloseAction = CrossAxisDeleteCloseAction.DeleteSelected;
        DialogResult = true;
    }

    private void OnPickInDrawing(object sender, RoutedEventArgs e)
    {
        CloseAction = CrossAxisDeleteCloseAction.PickInDrawing;
        DialogResult = true;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        CloseAction = CrossAxisDeleteCloseAction.Refresh;
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        CloseAction = CrossAxisDeleteCloseAction.Closed;
        DialogResult = false;
    }
}
