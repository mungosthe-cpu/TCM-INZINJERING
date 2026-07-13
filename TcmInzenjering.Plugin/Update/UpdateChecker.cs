using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcmInzenjering.Plugin.Update;

public sealed class UpdateCheckResult
{
    public bool CheckSucceeded { get; init; }
    public bool UpdateAvailable { get; init; }
    public string CurrentVersion { get; init; } = PluginInfo.Version;
    public string? LatestVersion { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ErrorMessage { get; init; }
}

internal static class UpdateChecker
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static DateTime _lastCheckUtc = DateTime.MinValue;
    private static UpdateCheckResult? _cachedResult;

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh &&
            _cachedResult is not null &&
            DateTime.UtcNow - _lastCheckUtc < TimeSpan.FromHours(6))
        {
            return _cachedResult;
        }

        try
        {
            using var response = await HttpClient.GetAsync(PluginInfo.UpdateManifestUrl).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions).ConfigureAwait(false);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return CacheResult(new UpdateCheckResult
                {
                    CheckSucceeded = false,
                    ErrorMessage = "Manifest nadogradnje nije validan."
                });
            }

            var current = ParseVersion(PluginInfo.Version);
            var latest = ParseVersion(manifest.Version);
            var updateAvailable = latest > current;

            return CacheResult(new UpdateCheckResult
            {
                CheckSucceeded = true,
                UpdateAvailable = updateAvailable,
                LatestVersion = manifest.Version,
                DownloadUrl = string.IsNullOrWhiteSpace(manifest.DownloadUrl)
                    ? PluginInfo.ReleasesPageUrl
                    : manifest.DownloadUrl,
                ReleaseNotes = manifest.Notes
            });
        }
        catch (Exception ex)
        {
            return CacheResult(new UpdateCheckResult
            {
                CheckSucceeded = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public static UpdateCheckResult CheckForUpdates(bool forceRefresh = false) =>
        CheckForUpdatesAsync(forceRefresh).GetAwaiter().GetResult();

    private static UpdateCheckResult CacheResult(UpdateCheckResult result)
    {
        _cachedResult = result;
        _lastCheckUtc = DateTime.UtcNow;
        return result;
    }

    private static Version ParseVersion(string value)
    {
        if (Version.TryParse(value, out var version))
        {
            return version;
        }

        return Version.Parse("0.0.0");
    }

    private sealed class UpdateManifest
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public string? Notes { get; set; }
        public string? ReleaseDate { get; set; }
    }
}
