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

    public const string AuthorName = "Dragan Todorović";
    public const string AuthorCity = "Ruma";
    public const string AuthorPhone = "+381 63/550450";
    public const string AuthorEmail = "dragan.todorovic@hotmail.com";
    public const string AuthorFacebookUrl = "https://www.facebook.com/search/top?q=Dragan%20Todorovic%20Ruma";
}
