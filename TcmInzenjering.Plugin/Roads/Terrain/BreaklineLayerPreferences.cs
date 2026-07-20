using System.IO;
using System.Text.Json;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Pamti poslednju XY toleranciju posebno za svaki breakline lejer.</summary>
internal static class BreaklineLayerPreferences
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TcmInzenjering",
        "breakline-layer-tolerances.json");

    private static Dictionary<string, double>? _values;

    public static double Get(string layerName)
    {
        EnsureLoaded();
        return _values!.TryGetValue(layerName, out var value) && value > 0
            ? value
            : 0.15;
    }

    public static void Set(string layerName, double tolerance)
    {
        if (string.IsNullOrWhiteSpace(layerName) || tolerance <= 0)
        {
            return;
        }

        EnsureLoaded();
        _values![layerName.Trim()] = tolerance;
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                _values,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Pamćenje je pomoćna funkcija; komanda nastavlja i bez fajla.
        }
    }

    private static void EnsureLoaded()
    {
        if (_values is not null)
        {
            return;
        }

        try
        {
            _values = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, double>>(
                      File.ReadAllText(FilePath),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? NewDictionary()
                : NewDictionary();
        }
        catch
        {
            _values = NewDictionary();
        }
    }

    private static Dictionary<string, double> NewDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);
}
