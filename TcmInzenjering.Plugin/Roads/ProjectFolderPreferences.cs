using System.IO;
using System.Text.Json;
using System.Windows;
using TcmInzenjering.Plugin.Dialogs;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Default folder projekta (kao Plateia project path) — snimanje 3DFACE / izlaznih fajlova.
/// </summary>
internal static class ProjectFolderPreferences
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
            "project-folder.json");

    public static string FolderPath { get; private set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TcmInzenjering");

    public static void Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(PreferencesPath), JsonOptions);
            if (!string.IsNullOrWhiteSpace(dto?.FolderPath))
            {
                FolderPath = dto.FolderPath.Trim();
            }
        }
        catch
        {
            // keep default
        }
    }

    public static void Save(string folderPath)
    {
        FolderPath = string.IsNullOrWhiteSpace(folderPath)
            ? FolderPath
            : folderPath.Trim();

        try
        {
            var dir = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                PreferencesPath,
                JsonSerializer.Serialize(new Dto { FolderPath = FolderPath }, JsonOptions));
        }
        catch
        {
            // best-effort
        }
    }

    public static bool TryPickFolder(Window? owner, out string folder)
    {
        folder = FolderPath;
        try
        {
            var dlg = new ProjectFolderDialog(FolderPath, owner);
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedFolder))
            {
                return false;
            }

            folder = dlg.SelectedFolder;
            Save(folder);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string EnsureFolder()
    {
        Directory.CreateDirectory(FolderPath);
        return FolderPath;
    }

    private sealed class Dto
    {
        public string FolderPath { get; set; } = string.Empty;
    }
}
