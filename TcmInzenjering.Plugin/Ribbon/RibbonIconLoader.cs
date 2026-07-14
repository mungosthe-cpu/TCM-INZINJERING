using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace TcmInzenjering.Plugin.Ribbon;

internal static class RibbonIconLoader
{
    private const int RibbonIconSize = 32;

    public static BitmapImage? Load(string iconName)
    {
        foreach (var directory in GetIconDirectories())
        {
            var path = Path.Combine(directory, $"{iconName}.png");
            if (!File.Exists(path))
            {
                continue;
            }

            return LoadFromPath(path);
        }

        if (string.Equals(iconName, "toolspace", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = GetIconDirectories()
                .Select(directory => Path.Combine(directory, "info.png"))
                .FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return LoadFromPath(fallback);
            }
        }

        return null;
    }

    private static BitmapImage LoadFromPath(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = RibbonIconSize;
        image.DecodePixelHeight = RibbonIconSize;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static IEnumerable<string> GetIconDirectories()
    {
        var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(dllDir))
        {
            yield break;
        }

        yield return Path.Combine(dllDir, "Icons");

        var contentsDir = Directory.GetParent(dllDir)?.FullName;
        if (!string.IsNullOrWhiteSpace(contentsDir))
        {
            yield return Path.Combine(contentsDir, "Icons");
            yield return Path.Combine(contentsDir, "net8", "Icons");
            yield return Path.Combine(contentsDir, "net48", "Icons");
        }
    }
}
