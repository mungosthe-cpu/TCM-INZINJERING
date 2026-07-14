using System.IO;
using System.Text.Json;

namespace TcmInzenjering.Plugin.Roads;

internal static class StationFontPreferences
{
    private const string DefaultFontFile = "txt.shx";

    private static JsonSerializerOptions? _jsonOptions;

    private static JsonSerializerOptions JsonOptions =>
        _jsonOptions ??= new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private static string PreferencesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "station-font.json");

    public static string FontFileName { get; private set; } = DefaultFontFile;

    public static void Load()
    {
        if (!File.Exists(PreferencesPath))
        {
            FontFileName = DefaultFontFile;
            return;
        }

        try
        {
            var json = File.ReadAllText(PreferencesPath);
            var saved = JsonSerializer.Deserialize<StationFontSettings>(json, JsonOptions);
            FontFileName = string.IsNullOrWhiteSpace(saved?.FontFileName) ? DefaultFontFile : saved.FontFileName.Trim();
        }
        catch
        {
            FontFileName = DefaultFontFile;
        }
    }

    public static void Save(string fontFileName)
    {
        FontFileName = string.IsNullOrWhiteSpace(fontFileName) ? DefaultFontFile : fontFileName.Trim();
        try
        {
            var directory = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new StationFontSettings { FontFileName = FontFileName }, JsonOptions);
            File.WriteAllText(PreferencesPath, json);
        }
        catch
        {
            // Ne blokiraj rad ako upis ne uspe.
        }
    }

    public sealed class StationFontSettings
    {
        public string FontFileName { get; set; } = DefaultFontFile;
    }
}
