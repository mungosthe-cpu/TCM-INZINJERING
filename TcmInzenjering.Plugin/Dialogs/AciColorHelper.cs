using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Dialogs;

internal static class AciColorHelper
{
    public static readonly short[] PickableColors = [1, 2, 3, 4, 5, 6, 7, 30, 40, 90, 130, 140, 200, 250];

    public static AcColor ToAcadColor(short aci) =>
        AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);

    public static Brush ToBrush(short aci)
    {
        var acad = ToAcadColor(aci);
        var rgb = acad.ColorValue;
        return new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
    }

    public static void ShowPicker(Button anchor, short currentAci, Action<short> onSelected)
    {
        var menu = new ContextMenu();
        foreach (var aci in PickableColors)
        {
            var preview = new Border
            {
                Width = 18,
                Height = 14,
                Background = ToBrush(aci),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0)
            };

            var item = new MenuItem
            {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        preview,
                        new TextBlock { Text = $"ACI {aci}", VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                Tag = aci,
                IsChecked = aci == currentAci
            };

            item.Click += (_, _) => onSelected(aci);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    public static void ApplyToButton(Button button, short aci)
    {
        button.Background = ToBrush(aci);
        button.BorderBrush = Brushes.Gray;
        button.BorderThickness = new Thickness(1);
        button.Tag = aci;
    }
}
