using System.IO;
using System.Text.Json;
using TcmInzenjering.Plugin.Dialogs;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Poslednje TCMTERBLOK mapiranje (blok + atribut Z) — za crtanje tačaka na granici.
/// </summary>
internal static class TerrainPointBlockPreferences
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
            "terrain-point-block.json");

    public static TerrainBlockPointMapping? Current { get; private set; }

    public static void Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                Current = null;
                return;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(PreferencesPath), JsonOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.BlockName) ||
                string.IsNullOrWhiteSpace(dto.ElevationAttributeTag))
            {
                Current = null;
                return;
            }

            Current = new TerrainBlockPointMapping
            {
                BlockName = dto.BlockName.Trim(),
                ElevationAttributeTag = dto.ElevationAttributeTag.Trim(),
                XySource = dto.XyFromAttributePosition
                    ? TerrainBlockXySource.ElevationAttributePosition
                    : TerrainBlockXySource.BlockInsertion
            };
        }
        catch
        {
            Current = null;
        }
    }

    public static void Save(TerrainBlockPointMapping mapping)
    {
        Current = mapping;
        try
        {
            var dir = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                PreferencesPath,
                JsonSerializer.Serialize(
                    new Dto
                    {
                        BlockName = mapping.BlockName,
                        ElevationAttributeTag = mapping.ElevationAttributeTag,
                        XyFromAttributePosition =
                            mapping.XySource == TerrainBlockXySource.ElevationAttributePosition
                    },
                    JsonOptions));
        }
        catch
        {
            // best-effort
        }
    }

    private sealed class Dto
    {
        public string BlockName { get; set; } = string.Empty;
        public string ElevationAttributeTag { get; set; } = string.Empty;
        public bool XyFromAttributePosition { get; set; }
    }
}
