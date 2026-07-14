using System.Drawing.Text;
using System.IO;

namespace TcmInzenjering.Plugin.Roads;

internal sealed record StationFontOption
{
    public string DisplayName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}

internal static class StationFontCatalog
{
    private static readonly Dictionary<string, string> KnownFamilyFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Times New Roman"] = "times.ttf",
        ["Arial"] = "arial.ttf",
        ["Calibri"] = "calibri.ttf",
        ["Courier New"] = "cour.ttf",
        ["Verdana"] = "verdana.ttf",
        ["Tahoma"] = "tahoma.ttf",
        ["Segoe UI"] = "segoeui.ttf",
        ["Georgia"] = "georgia.ttf",
        ["Trebuchet MS"] = "trebuc.ttf",
        ["Century Gothic"] = "gothic.ttf",
        ["ISOCPEUR"] = "isocpeur.ttf"
    };

    public static IReadOnlyList<StationFontOption> Load()
    {
        var options = new Dictionary<string, StationFontOption>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in GetFontDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".shx", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = Path.GetFileName(file);
                options[fileName] = new StationFontOption
                {
                    FileName = fileName,
                    DisplayName = fileName
                };
            }
        }

        foreach (var pair in KnownFamilyFiles)
        {
            if (options.TryGetValue(pair.Value, out var existing))
            {
                options[pair.Value] = existing with { DisplayName = $"{pair.Key} ({pair.Value})" };
            }
            else
            {
                options[pair.Value] = new StationFontOption
                {
                    FileName = pair.Value,
                    DisplayName = $"{pair.Key} ({pair.Value})"
                };
            }
        }

        try
        {
            using var installed = new InstalledFontCollection();
            foreach (var family in installed.Families.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (KnownFamilyFiles.TryGetValue(family.Name, out var mappedFile) &&
                    options.TryGetValue(mappedFile, out var mapped))
                {
                    options[mappedFile] = mapped with { DisplayName = $"{family.Name} ({mappedFile})" };
                    continue;
                }

                var matchedFile = options.Values
                    .FirstOrDefault(option =>
                        option.FileName.StartsWith(family.Name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(option.FileName), family.Name, StringComparison.OrdinalIgnoreCase));

                if (matchedFile is not null)
                {
                    options[matchedFile.FileName] = matchedFile with
                    {
                        DisplayName = $"{family.Name} ({matchedFile.FileName})"
                    };
                }
            }
        }
        catch
        {
            // Font enumeration is best-effort.
        }

        return options.Values
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ResolveFileName(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return StationFontPreferences.FontFileName;
        }

        var trimmed = selection.Trim();
        var catalog = Load();
        var match = catalog.FirstOrDefault(option =>
            string.Equals(option.FileName, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match.FileName;
        }

        if (trimmed.Contains("(") && trimmed.EndsWith(")"))
        {
            var start = trimmed.LastIndexOf('(');
            var inner = trimmed.Substring(start + 1, trimmed.Length - start - 2).Trim();
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return inner;
            }
        }

        return trimmed;
    }

    private static IEnumerable<string> GetFontDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Autodesk", "AutoCAD 2026", "Fonts");
        yield return Path.Combine(programFiles, "Autodesk", "AutoCAD 2025", "Fonts");
        yield return Path.Combine(programFiles, "Autodesk", "AutoCAD 2024", "Fonts");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
    }
}
