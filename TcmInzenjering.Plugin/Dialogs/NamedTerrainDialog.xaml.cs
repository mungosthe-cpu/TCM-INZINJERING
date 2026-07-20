using System.Windows;
using System.Windows.Controls;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class NamedTerrainDialog : Window
{
    public string TerrainName { get; private set; } = "";

    public NamedTerrainDialog(
        IReadOnlyList<string> existingNames,
        string suggestedName,
        string? hint = null,
        Window? owner = null)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        foreach (var name in existingNames)
        {
            NameBox.Items.Add(name);
        }

        NameBox.Text = suggestedName;
        HintText.Text = hint
                        ?? (existingNames.Count > 0
                            ? "Izaberite postojece ime da azurirate taj teren, ili unesite novo."
                            : "Npr. Teren_1, Put_A, Plato…");
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Unesite ime terena.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TerrainName = name;
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
