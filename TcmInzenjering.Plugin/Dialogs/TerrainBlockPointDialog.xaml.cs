using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace TcmInzenjering.Plugin.Dialogs;

public sealed class TerrainBlockAttributeRow
{
    public required string Tag { get; init; }
    public required string Value { get; init; }
}

public enum TerrainBlockXySource
{
    BlockInsertion,
    ElevationAttributePosition
}

public sealed class TerrainBlockPointMapping
{
    public required string BlockName { get; init; }
    public required string ElevationAttributeTag { get; init; }
    public TerrainBlockXySource XySource { get; init; }
}

public partial class TerrainBlockPointDialog : Window
{
    private readonly ObservableCollection<TerrainBlockAttributeRow> _attrs;

    public TerrainBlockPointMapping? Result { get; private set; }

    public TerrainBlockPointDialog(
        string blockName,
        IReadOnlyList<TerrainBlockAttributeRow> attributes,
        Window? owner = null)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        BlockNameText.Text = blockName;
        _attrs = new ObservableCollection<TerrainBlockAttributeRow>(attributes);
        AttrsGrid.ItemsSource = _attrs;
        ElevationAttrBox.ItemsSource = _attrs;

        if (_attrs.Count > 0)
        {
            var preferred = _attrs.FirstOrDefault(a =>
                                LooksLikeElevation(a.Tag) || LooksLikeElevationValue(a.Value))
                            ?? _attrs[0];
            ElevationAttrBox.SelectedItem = preferred;
            AttrsGrid.SelectedItem = preferred;
        }
    }

    private static bool LooksLikeElevation(string tag)
    {
        var t = tag.Trim().ToUpperInvariant();
        return t is "Z" or "H" or "ELEV" or "ELEVATION" or "VISINA" or "KOTA" or "KOTAC"
            or "HEIGHT" or "RL";
    }

    private static bool LooksLikeElevationValue(string value) =>
        double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private void OnAttrSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AttrsGrid.SelectedItem is TerrainBlockAttributeRow row)
        {
            ElevationAttrBox.SelectedItem = row;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (ElevationAttrBox.SelectedItem is not TerrainBlockAttributeRow row)
        {
            MessageBox.Show(this, "Izaberite atribut koji sadrzi visinu.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new TerrainBlockPointMapping
        {
            BlockName = BlockNameText.Text.Trim(),
            ElevationAttributeTag = row.Tag,
            XySource = XyAttrRadio.IsChecked == true
                ? TerrainBlockXySource.ElevationAttributePosition
                : TerrainBlockXySource.BlockInsertion
        };

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
