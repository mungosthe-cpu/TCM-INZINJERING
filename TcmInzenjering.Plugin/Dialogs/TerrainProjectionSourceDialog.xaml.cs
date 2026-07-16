using System.Windows;
using System.Windows.Input;

namespace TcmInzenjering.Plugin.Dialogs;

public enum TerrainProjectionSourceCloseAction
{
    Cancelled = 0,
    Confirmed = 1,
    PickInDrawing = 2
}

public partial class TerrainProjectionSourceDialog : Window
{
    public TerrainProjectionSourceCloseAction CloseAction { get; private set; } =
        TerrainProjectionSourceCloseAction.Cancelled;

    public string? SelectedTerrainName { get; private set; }

    public TerrainProjectionSourceDialog(
        IReadOnlyList<string> terrainNames,
        string? activeName,
        Window? owner = null)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        foreach (var name in terrainNames)
        {
            TerrainList.Items.Add(name);
        }

        if (TerrainList.Items.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            var prefer = !string.IsNullOrWhiteSpace(activeName)
                ? activeName
                : terrainNames[0];
            var idx = -1;
            for (var i = 0; i < TerrainList.Items.Count; i++)
            {
                if (string.Equals(TerrainList.Items[i]?.ToString(), prefer, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            TerrainList.SelectedIndex = idx >= 0 ? idx : 0;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();

    private void OnOk(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (TerrainList.SelectedItem is not string name || string.IsNullOrWhiteSpace(name))
        {
            if (TerrainList.Items.Count == 0)
            {
                MessageBox.Show(this,
                    "Nema snimljenih terena u listi.\nKoristite „Izaberi u crtezu…“ ili prvo napravite teren (TCMTERFACE).",
                    "TCM-INŽINJERING", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBox.Show(this, "Izaberite teren iz liste.", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedTerrainName = name.Trim();
        CloseAction = TerrainProjectionSourceCloseAction.Confirmed;
        TryClose(true);
    }

    private void OnPickInDrawing(object sender, RoutedEventArgs e)
    {
        CloseAction = TerrainProjectionSourceCloseAction.PickInDrawing;
        SelectedTerrainName = null;
        TryClose(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseAction = TerrainProjectionSourceCloseAction.Cancelled;
        TryClose(false);
    }

    private void TryClose(bool dialogResult)
    {
        try
        {
            DialogResult = dialogResult;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
