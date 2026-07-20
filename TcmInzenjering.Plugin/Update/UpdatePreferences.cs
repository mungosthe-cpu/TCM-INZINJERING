using System.IO;
using System.Text.Json;

namespace TcmInzenjering.Plugin.Update;

/// <summary>Korisničke preferencije za proveru nadogradnje.</summary>
internal static class UpdatePreferences
{
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string PreferencesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "update-preferences.json");

    /// <summary>Da li se pri startu AutoCAD-a tiho proverava nova verzija.</summary>
    public static bool CheckOnStartup { get; private set; } = true;

    public static void Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(PreferencesPath), JsonOptions);
            if (dto is not null)
            {
                CheckOnStartup = dto.CheckOnStartup;
            }
        }
        catch
        {
            // keep default
        }
    }

    public static void Save(bool checkOnStartup)
    {
        CheckOnStartup = checkOnStartup;
        try
        {
            var dir = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                PreferencesPath,
                JsonSerializer.Serialize(new Dto { CheckOnStartup = CheckOnStartup }, JsonOptions));
        }
        catch
        {
            // best-effort
        }
    }

    private sealed class Dto
    {
        public bool CheckOnStartup { get; set; } = true;
    }
}
