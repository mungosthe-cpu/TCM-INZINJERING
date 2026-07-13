using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace TcmInzenjering.Plugin.Ribbon;

internal static class RibbonIconLoader
{
    public static BitmapImage? Load(string iconName)
    {
        foreach (var directory in GetIconDirectories())
        {
            var path = Path.Combine(directory, $"{iconName}.png");
            if (!File.Exists(path))
            {
                continue;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        return null;
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
