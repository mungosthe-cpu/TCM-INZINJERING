using System.IO;
using System.Text.Json;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Globalna podesavanja: font stacionaze + duzina poprecnih osa (levo/desno od osovine).
/// </summary>
internal static class StationFontPreferences
{
    private const string DefaultFontFile = "txt.shx";
    public const double DefaultHalfTickLength = RoadDrawing.DefaultTickLength / 2.0;

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
            "plugin-settings.json");

    private static string LegacyFontPreferencesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "station-font.json");

    public static string FontFileName { get; private set; } = DefaultFontFile;

    /// <summary>Duzina poprecne ose levo od putne ose (u smeru rasta stacionaze).</summary>
    public static double CrossAxisLeftLength { get; private set; } = DefaultHalfTickLength;

    /// <summary>Duzina poprecne ose desno od putne ose (u smeru rasta stacionaze).</summary>
    public static double CrossAxisRightLength { get; private set; } = DefaultHalfTickLength;

    public static double TotalTickLength =>
        Math.Max(0.1, CrossAxisLeftLength + CrossAxisRightLength);

    public static void Load()
    {
        if (!File.Exists(PreferencesPath) && File.Exists(LegacyFontPreferencesPath))
        {
            TryLoadFromPath(LegacyFontPreferencesPath);
            Save(FontFileName, CrossAxisLeftLength, CrossAxisRightLength);
            return;
        }

        if (!File.Exists(PreferencesPath))
        {
            FontFileName = DefaultFontFile;
            CrossAxisLeftLength = DefaultHalfTickLength;
            CrossAxisRightLength = DefaultHalfTickLength;
            return;
        }

        TryLoadFromPath(PreferencesPath);
    }

    public static void Save(string fontFileName, double leftLength, double rightLength)
    {
        FontFileName = string.IsNullOrWhiteSpace(fontFileName) ? DefaultFontFile : fontFileName.Trim();
        CrossAxisLeftLength = Math.Max(0.1, leftLength);
        CrossAxisRightLength = Math.Max(0.1, rightLength);

        try
        {
            var directory = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                new PluginSettings
                {
                    FontFileName = FontFileName,
                    CrossAxisLeftLength = CrossAxisLeftLength,
                    CrossAxisRightLength = CrossAxisRightLength
                },
                JsonOptions);
            File.WriteAllText(PreferencesPath, json);
        }
        catch
        {
            // Ne blokiraj rad ako upis ne uspe.
        }
    }

    /// <summary>Kompatibilnost sa starim pozivima koji cuvaju samo font.</summary>
    public static void Save(string fontFileName) =>
        Save(fontFileName, CrossAxisLeftLength, CrossAxisRightLength);

    private static void TryLoadFromPath(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var saved = JsonSerializer.Deserialize<PluginSettings>(json, JsonOptions);
            FontFileName = string.IsNullOrWhiteSpace(saved?.FontFileName)
                ? DefaultFontFile
                : saved.FontFileName.Trim();
            CrossAxisLeftLength = saved?.CrossAxisLeftLength is > 0
                ? saved.CrossAxisLeftLength
                : DefaultHalfTickLength;
            CrossAxisRightLength = saved?.CrossAxisRightLength is > 0
                ? saved.CrossAxisRightLength
                : DefaultHalfTickLength;
        }
        catch
        {
            FontFileName = DefaultFontFile;
            CrossAxisLeftLength = DefaultHalfTickLength;
            CrossAxisRightLength = DefaultHalfTickLength;
        }
    }

    public sealed class PluginSettings
    {
        public string FontFileName { get; set; } = DefaultFontFile;
        public double CrossAxisLeftLength { get; set; } = DefaultHalfTickLength;
        public double CrossAxisRightLength { get; set; } = DefaultHalfTickLength;

        // Legacy field name still deserializes via camelCase FontFileName.
        public string? FontFile { get; set; }
    }
}
