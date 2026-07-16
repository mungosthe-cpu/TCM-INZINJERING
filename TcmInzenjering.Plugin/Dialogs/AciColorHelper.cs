using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Dialogs;

/// <summary>Civil-like ACI boja: swatch + ime (red/cyan/BYLAYER/42…).</summary>
internal static class AciColorHelper
{
    public static readonly short[] PickableColors =
    [
        1, 2, 3, 4, 5, 6, 7,
        8, 9, 30, 40, 42, 50, 60, 80, 90, 100, 120, 130, 140, 150, 160, 170, 180, 200, 210, 230, 250
    ];

    private static readonly Dictionary<short, string> StandardNames = new()
    {
        [1] = "red",
        [2] = "yellow",
        [3] = "green",
        [4] = "cyan",
        [5] = "blue",
        [6] = "magenta",
        [7] = "white"
    };

    public static AcColor ToAcadColor(short aci) =>
        AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);

    public static Brush ToBrush(short aci)
    {
        var acad = ToAcadColor(aci);
        var rgb = acad.ColorValue;
        return new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
    }

    public static Brush ToDisplayBrush(short aci, bool byLayer, bool byBlock = false)
    {
        if (byLayer || byBlock)
        {
            // Civil: crno-beli / sivi kvadrat za BYLAYER/BYBLOCK
            return new LinearGradientBrush(
                Colors.Black, Colors.White, new Point(0, 0), new Point(1, 1));
        }

        return ToBrush(aci is > 0 and < 256 ? aci : (short)7);
    }

    /// <summary>Kao Civil Color kolona: red / cyan / BYLAYER / 42…</summary>
    public static string ToDisplayName(short aci, bool byLayer, bool byBlock = false)
    {
        if (byLayer)
        {
            return "BYLAYER";
        }

        if (byBlock)
        {
            return "BYBLOCK";
        }

        if (StandardNames.TryGetValue(aci, out var name))
        {
            return name;
        }

        return aci.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void ShowPicker(Button anchor, short currentAci, Action<short> onSelected) =>
        ShowSelectColor(anchor, currentAci, byLayer: false, byBlock: false, result =>
        {
            if (!result.ByLayer && !result.ByBlock)
            {
                onSelected(result.Aci);
            }
        });

    public static void ShowSelectColor(
        FrameworkElement? owner,
        short currentAci,
        bool byLayer,
        bool byBlock,
        Action<ColorPickResult> onSelected)
    {
        var dialog = new SelectColorDialog(currentAci, byLayer, byBlock);
        if (owner is not null)
        {
            try
            {
                dialog.Owner = Window.GetWindow(owner);
            }
            catch
            {
                // ignore
            }
        }

        if (dialog.ShowDialog() == true)
        {
            onSelected(dialog.Result);
        }
    }

    public static void ApplyToButton(Button button, short aci)
    {
        button.Background = ToBrush(aci);
        button.BorderBrush = Brushes.Gray;
        button.BorderThickness = new Thickness(1);
        button.Tag = aci;
        button.Content = ToDisplayName(aci, byLayer: false);
        button.Foreground = ContrastForeground(aci);
        button.FontSize = 10;
    }

    public static Brush ContrastForeground(short aci)
    {
        var acad = ToAcadColor(aci);
        var rgb = acad.ColorValue;
        var luma = (0.299 * rgb.R) + (0.587 * rgb.G) + (0.114 * rgb.B);
        return luma > 140 ? Brushes.Black : Brushes.White;
    }

    public readonly struct ColorPickResult
    {
        public short Aci { get; init; }
        public bool ByLayer { get; init; }
        public bool ByBlock { get; init; }
    }
}
