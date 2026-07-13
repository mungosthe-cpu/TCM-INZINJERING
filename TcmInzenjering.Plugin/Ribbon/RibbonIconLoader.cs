using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace TcmInzenjering.Plugin.Ribbon;

internal static class RibbonIconLoader
{
    private static readonly string IconDirectory = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
        "Icons");

    public static BitmapImage? Load(string iconName)
    {
        var path = Path.Combine(IconDirectory, $"{iconName}.png");
        if (!File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
