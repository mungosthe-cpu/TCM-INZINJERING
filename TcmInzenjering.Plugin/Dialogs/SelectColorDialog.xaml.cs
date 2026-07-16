using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class SelectColorDialog : Window
{
    private short _aci;
    private bool _byLayer;
    private bool _byBlock;

    internal AciColorHelper.ColorPickResult Result { get; private set; }

    public SelectColorDialog(short currentAci, bool byLayer, bool byBlock)
    {
        InitializeComponent();
        _aci = currentAci is > 0 and < 256 ? currentAci : (short)7;
        _byLayer = byLayer;
        _byBlock = byBlock;
        BuildPalettes();
        RefreshPreview();
    }

    private void BuildPalettes()
    {
        foreach (short aci in new short[] { 1, 2, 3, 4, 5, 6, 7 })
        {
            StandardGrid.Children.Add(MakeSwatch(aci, large: true));
        }

        // Civil-lite palette: named extras + densely sampled ACI.
        for (short aci = 8; aci <= 255; aci++)
        {
            if (aci is >= 10 and <= 249 && (aci - 10) % 5 != 0 && !AciColorHelper.PickableColors.Contains(aci))
            {
                continue;
            }

            IndexGrid.Children.Add(MakeSwatch(aci, large: false));
        }

        // Ensure common Civil colors used in surface style are always present.
        foreach (short aci in AciColorHelper.PickableColors)
        {
            if (aci is >= 1 and <= 7)
            {
                continue;
            }

            if (!IndexGrid.Children.OfType<Border>().Any(b => b.Tag is short s && s == aci))
            {
                IndexGrid.Children.Add(MakeSwatch(aci, large: false));
            }
        }
    }

    private Border MakeSwatch(short aci, bool large)
    {
        var border = new Border
        {
            Width = large ? 36 : 22,
            Height = large ? 28 : 18,
            Margin = new Thickness(2),
            Background = AciColorHelper.ToBrush(aci),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = AciColorHelper.ToDisplayName(aci, byLayer: false),
            Tag = aci
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            _aci = aci;
            _byLayer = false;
            _byBlock = false;
            RefreshPreview();
        };
        return border;
    }

    private void RefreshPreview()
    {
        ColorNameBox.Text = AciColorHelper.ToDisplayName(_aci, _byLayer, _byBlock);
        PreviewBorder.Background = AciColorHelper.ToDisplayBrush(_aci, _byLayer, _byBlock);
    }

    private void OnByLayer(object sender, RoutedEventArgs e)
    {
        _byLayer = true;
        _byBlock = false;
        RefreshPreview();
    }

    private void OnByBlock(object sender, RoutedEventArgs e)
    {
        _byBlock = true;
        _byLayer = false;
        RefreshPreview();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new AciColorHelper.ColorPickResult
        {
            Aci = _aci,
            ByLayer = _byLayer,
            ByBlock = _byBlock
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
