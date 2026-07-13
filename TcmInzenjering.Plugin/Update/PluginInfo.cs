namespace TcmInzenjering.Plugin.Update;

public static class PluginInfo
{
    /// <summary>
    /// GitHub korisnik/organizacija i repozitorijum za proveru nadogradnje.
    /// Izmeni pre objavljivanja na GitHub.
    /// </summary>
    public const string GitHubOwner = "mungosthe-cpu";
    public const string GitHubRepo = "TCM-INZINJERING";

    public static string UpdateManifestUrl =>
        $"https://raw.githubusercontent.com/{GitHubOwner}/{GitHubRepo}/main/release/update-manifest.json";

    public static string ReleasesPageUrl =>
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static string Version => typeof(PluginInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
