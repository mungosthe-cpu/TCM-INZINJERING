using System.Windows;

namespace TcmInzenjering.Plugin.Dialogs;

public enum AxisSelectionCloseAction
{
    Cancelled,
    Selected,
    PickInDrawing
}

public partial class AxisSelectionDialog : Window
{
    public AxisSelectionDialog(IEnumerable<string> axisNames)
    {
        InitializeComponent();
        var names = axisNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        AxisBox.ItemsSource = names;
        if (names.Count > 0)
        {
            AxisBox.SelectedIndex = 0;
        }
    }

    public AxisSelectionCloseAction CloseAction { get; private set; } =
        AxisSelectionCloseAction.Cancelled;

    public string SelectedAxisName =>
        AxisBox.SelectedItem?.ToString() ?? string.Empty;

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedAxisName))
        {
            MessageBox.Show(
                this,
                "Izaberite osovinu ili koristite dugme „Izaberi u crtežu“.",
                "TCM-ROADS",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        CloseAction = AxisSelectionCloseAction.Selected;
        DialogResult = true;
    }

    private void OnPickInDrawing(object sender, RoutedEventArgs e)
    {
        CloseAction = AxisSelectionCloseAction.PickInDrawing;
        DialogResult = false;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseAction = AxisSelectionCloseAction.Cancelled;
        DialogResult = false;
    }
}
