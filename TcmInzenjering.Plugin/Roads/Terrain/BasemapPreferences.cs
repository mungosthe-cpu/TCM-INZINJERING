using System.IO;
using System.Text.Json;
using TcmInzenjering.Plugin.Dialogs;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal sealed class BasemapPreferencesSnapshot
{
    public BasemapMode LastMode { get; set; } = BasemapMode.Autodesk;
    public string MapStyleTag { get; set; } = "EsriImagery";
    public BasemapAutodeskAction AutodeskAction { get; set; } = BasemapAutodeskAction.CaptureViewport;
    public BasemapExternalSource ExternalSource { get; set; } = BasemapExternalSource.ArcGisWorld;
    public string ServiceUrl { get; set; } = BasemapPreferences.DefaultArcGisWorldUrl;
    public string WmsLayer { get; set; } = string.Empty;
    public string LocalFilePath { get; set; } = string.Empty;
    public BasemapAreaMode AreaMode { get; set; } = BasemapAreaMode.Pick;
    public int ResolutionPx { get; set; } = 2048;
    public byte OpacityPercent { get; set; } = 85;
    public BasemapDrawingCrs DrawingCrs { get; set; } = BasemapDrawingCrs.AutoGk;
}

internal static class BasemapPreferences
{
    public const string DefaultArcGisWorldUrl =
        "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer";

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string PreferencesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "basemap-settings.json");

    public static BasemapPreferencesSnapshot Current { get; private set; } = new();

    static BasemapPreferences()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<BasemapPreferencesSnapshot>(
                File.ReadAllText(PreferencesPath), JsonOptions);
            if (dto is not null)
            {
                Current = dto;
            }
        }
        catch
        {
            // keep defaults
        }
    }

    public static void Save(BasemapSettings settings)
    {
        Current = new BasemapPreferencesSnapshot
        {
            LastMode = settings.Mode,
            MapStyleTag = settings.MapStyleTag,
            AutodeskAction = settings.AutodeskAction,
            ExternalSource = settings.ExternalSource,
            ServiceUrl = settings.ServiceUrl,
            WmsLayer = settings.WmsLayer,
            LocalFilePath = settings.LocalFilePath,
            AreaMode = settings.AreaMode,
            ResolutionPx = settings.ResolutionPx,
            OpacityPercent = settings.OpacityPercent,
            DrawingCrs = settings.DrawingCrs
        };

        try
        {
            var dir = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // best-effort
        }
    }
}
