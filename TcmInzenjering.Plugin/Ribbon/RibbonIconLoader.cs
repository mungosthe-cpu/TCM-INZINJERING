using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace TcmInzenjering.Plugin.Ribbon;

internal static class RibbonIconLoader
{
    // AutoCAD Ribbon Large = 32×32, Small = 16×16 (standard AdWindows).
    private const int LargePx = 32;
    private const int SmallPx = 16;

    public static BitmapImage? LoadLarge(string iconName) => Load(iconName, LargePx);

    public static BitmapImage? LoadSmall(string iconName) => Load(iconName, SmallPx);

    public static BitmapImage? Load(string iconName) => LoadLarge(iconName);

    /// <summary>Učitava PNG bez DecodePixel* — native rezolucija fajla (za situacija_32 / _16).</summary>
    public static BitmapImage? LoadNative(string iconName)
    {
        foreach (var directory in GetIconDirectories())
        {
            var path = Path.Combine(directory, $"{iconName}.png");
            if (!File.Exists(path))
            {
                continue;
            }

            return LoadFromPath(path, decodeSize: null);
        }

        return null;
    }

    public static BitmapImage? Load(string iconName, int size)
    {
        foreach (var directory in GetIconDirectories())
        {
            var path = Path.Combine(directory, $"{iconName}.png");
            if (!File.Exists(path))
            {
                continue;
            }

            return LoadFromPath(path, size);
        }

        if (string.Equals(iconName, "toolspace", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(iconName, "projekcija", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var directory in GetIconDirectories())
            {
                var fallback = Path.Combine(directory, "info.png");
                if (File.Exists(fallback))
                {
                    return LoadFromPath(fallback, size);
                }
            }
        }

        return null;
    }

    public static (BitmapImage? Large, BitmapImage? Small) LoadPair(string iconName) =>
        (LoadLarge(iconName), LoadSmall(iconName));

    private static BitmapImage LoadFromPath(string path, int? decodeSize)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeSize is int size)
        {
            image.DecodePixelWidth = size;
            image.DecodePixelHeight = size;
        }

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
