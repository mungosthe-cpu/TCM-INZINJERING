using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>Sadržaj rubrike u podatkovnom delu tabele.</summary>
public enum ProfileBandContent
{
    /// <summary>Razmak između stacionaža (OZNAKE PROFILA).</summary>
    ProfileMarks = 0,

    /// <summary>Stacionaža.</summary>
    Stations = 1,

    /// <summary>Kota terena.</summary>
    TerrainElevations = 2,

    /// <summary>Prazna rubrika (samo naslov).</summary>
    Blank = 3,

    /// <summary>Visinske kote projektovane nivelete (kad postoji vertikalni profil).</summary>
    GradeElevations = 4,

    /// <summary>Širine traka kolovoza levo/desno u jednoj rubrici.</summary>
    LaneWidths = 5,

    /// <summary>Glavni elementi osovine (pravci i lukovi) iz crteža.</summary>
    MainElements = 6
}

internal static class ProfileBandContentLabels
{
    public static IReadOnlyList<ProfileBandContentChoice> All { get; } =
    [
        new(ProfileBandContent.ProfileMarks, "Oznake profila"),
        new(ProfileBandContent.Stations, "Stacionaze"),
        new(ProfileBandContent.TerrainElevations, "Kote terena"),
        new(ProfileBandContent.GradeElevations, "Kote nivelete"),
        new(ProfileBandContent.LaneWidths, "Sirine traka (L/D)"),
        new(ProfileBandContent.MainElements, "Glavni elementi"),
        new(ProfileBandContent.Blank, "Prazno")
    ];
}

public sealed record ProfileBandContentChoice(ProfileBandContent Value, string Label);

/// <summary>Jedna horizontalna rubrika tipa tabele.</summary>
public sealed class ProfileTableBand
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ProfileBandContent Content { get; set; } = ProfileBandContent.Blank;
    /// <summary>Visina rubrike u crtežu (npr. 10).</summary>
    public double Height { get; set; } = 10.0;
    public short TextAci { get; set; } = 7;
}

/// <summary>Snimljivi tip tabele (TCM_1, korisnički…).</summary>
public sealed class ProfileTableType
{
    public string Name { get; set; } = "TCM_1";
    /// <summary>Širina kolone naziva rubrika (npr. 50).</summary>
    public double LabelColumnWidth { get; set; } = 50.0;
    /// <summary>Podrazumevana visina nove rubrike.</summary>
    public double DefaultBandHeight { get; set; } = 10.0;
    /// <summary>
    /// Rubrike od vrha tabele (uz grafik) nadole.
    /// Prva = najbliža grafiku; poslednja = dno okvira.
    /// </summary>
    public List<ProfileTableBand> Bands { get; set; } = [];

    public ProfileTableType Clone()
    {
        return new ProfileTableType
        {
            Name = Name,
            LabelColumnWidth = LabelColumnWidth,
            DefaultBandHeight = DefaultBandHeight,
            Bands = Bands.Select(b => new ProfileTableBand
            {
                Code = b.Code,
                Title = b.Title,
                Content = b.Content,
                Height = b.Height,
                TextAci = b.TextAci
            }).ToList()
        };
    }

    public static ProfileTableType CreateDefaultTcm1() =>
        new()
        {
            Name = "TCM_1",
            LabelColumnWidth = 50.0,
            DefaultBandHeight = 10.0,
            Bands =
            [
                new ProfileTableBand
                {
                    Code = "LK_1",
                    Title = "OZNAKE PROFILA",
                    Content = ProfileBandContent.ProfileMarks,
                    Height = 10.0,
                    TextAci = 7
                },
                new ProfileTableBand
                {
                    Code = "LK_2",
                    Title = "STACIONAZE",
                    Content = ProfileBandContent.Stations,
                    Height = 10.0,
                    TextAci = 7
                },
                new ProfileTableBand
                {
                    Code = "LK_3",
                    Title = "KOTE TERENA",
                    Content = ProfileBandContent.TerrainElevations,
                    Height = 10.0,
                    TextAci = 3
                },
                new ProfileTableBand
                {
                    Code = "LK_4",
                    Title = "KOTE NIVELETE",
                    Content = ProfileBandContent.GradeElevations,
                    Height = 10.0,
                    TextAci = 1
                },
                new ProfileTableBand
                {
                    Code = "LK_5",
                    Title = "SIRINE TRAKA",
                    Content = ProfileBandContent.LaneWidths,
                    Height = 10.0,
                    TextAci = 7
                }
            ]
        };
}

/// <summary>Učitava / snima tipove tabela u %APPDATA%\TcmInzenjering\profile-table-types.json.</summary>
internal static class ProfileTableTypeCatalog
{
    private static readonly object Sync = new();
    private static List<ProfileTableType>? _cache;

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "profile-table-types.json");

    public static IReadOnlyList<ProfileTableType> GetAll()
    {
        lock (Sync)
        {
            EnsureLoaded();
            return _cache!.Select(t => t.Clone()).ToList();
        }
    }

    public static IReadOnlyList<string> GetNames()
    {
        lock (Sync)
        {
            EnsureLoaded();
            return _cache!.Select(t => t.Name).ToList();
        }
    }

    public static ProfileTableType Resolve(string? name)
    {
        lock (Sync)
        {
            EnsureLoaded();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var found = _cache!.FirstOrDefault(t =>
                    string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                {
                    return found.Clone();
                }
            }

            return ProfileTableType.CreateDefaultTcm1();
        }
    }

    public static void SaveAll(IEnumerable<ProfileTableType> types)
    {
        lock (Sync)
        {
            var list = types
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .Select(Normalize)
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.All(t => !string.Equals(t.Name, "TCM_1", StringComparison.OrdinalIgnoreCase)))
            {
                list.Insert(0, ProfileTableType.CreateDefaultTcm1());
            }

            _cache = list;
            WriteFile(_cache);
        }
    }

    public static void Upsert(ProfileTableType type)
    {
        lock (Sync)
        {
            EnsureLoaded();
            var normalized = Normalize(type);
            var idx = _cache!.FindIndex(t =>
                string.Equals(t.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _cache[idx] = normalized;
            }
            else
            {
                _cache.Add(normalized);
            }

            WriteFile(_cache);
        }
    }

    public static bool Delete(string name)
    {
        if (string.Equals(name, "TCM_1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Sync)
        {
            EnsureLoaded();
            var removed = _cache!.RemoveAll(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                WriteFile(_cache);
            }

            return removed > 0;
        }
    }

    public static void Reload()
    {
        lock (Sync)
        {
            _cache = null;
            EnsureLoaded();
        }
    }

    private static void EnsureLoaded()
    {
        if (_cache is not null)
        {
            return;
        }

        _cache = [];
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                var loaded = JsonSerializer.Deserialize<List<ProfileTableType>>(json, JsonOptions);
                if (loaded is { Count: > 0 })
                {
                    _cache = loaded.Select(Normalize).ToList();
                }
            }
        }
        catch
        {
            _cache = [];
        }

        if (_cache.All(t => !string.Equals(t.Name, "TCM_1", StringComparison.OrdinalIgnoreCase)))
        {
            _cache.Insert(0, ProfileTableType.CreateDefaultTcm1());
        }
    }

    private static void WriteFile(List<ProfileTableType> types)
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(StorePath, JsonSerializer.Serialize(types, JsonOptions));
        }
        catch
        {
            // preferences best-effort
        }
    }

    private static ProfileTableType Normalize(ProfileTableType type)
    {
        var clone = type.Clone();
        clone.Name = clone.Name.Trim();
        clone.LabelColumnWidth = Math.Max(5.0, clone.LabelColumnWidth);
        clone.DefaultBandHeight = Math.Max(1.0, clone.DefaultBandHeight);
        if (clone.Bands.Count == 0)
        {
            clone.Bands = ProfileTableType.CreateDefaultTcm1().Bands;
        }

        for (var i = 0; i < clone.Bands.Count; i++)
        {
            var b = clone.Bands[i];
            if (string.IsNullOrWhiteSpace(b.Code))
            {
                b.Code = $"LK_{i + 1}";
            }

            if (string.IsNullOrWhiteSpace(b.Title))
            {
                b.Title = b.Code;
            }

            b.Height = Math.Max(1.0, b.Height);
            if (b.TextAci < 1)
            {
                b.TextAci = 7;
            }
        }

        return clone;
    }
}

/// <summary>Kompatibilni alias — stari kod još koristi ove konstante.</summary>
internal enum ProfileBandKind
{
    ProfileMarks = 0,
    Stations = 1,
    TerrainElevations = 2,
    Blank = 3,
    GradeElevations = 4,
    LaneWidths = 5,
    MainElements = 6
}

internal sealed class ProfileBandDefinition
{
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public ProfileBandKind Kind { get; init; }
    public double Height { get; init; }
    public short TextAci { get; init; } = 7;

    public const double StandardBandHeight = 10.0;
    public const double LabelColumnWidth = 50.0;

    public static IReadOnlyList<ProfileBandDefinition> Tcm1() =>
        FromTableType(ProfileTableType.CreateDefaultTcm1());

    public static IReadOnlyList<ProfileBandDefinition> FromTableType(ProfileTableType type) =>
        type.Bands.Select(b => new ProfileBandDefinition
        {
            Code = b.Code,
            Title = b.Title,
            Kind = (ProfileBandKind)(int)b.Content,
            Height = b.Height > 1e-6 ? b.Height : type.DefaultBandHeight,
            TextAci = b.TextAci
        }).ToList();
}

/// <summary>Rezultat dijaloga Unos terena (CGSA stil).</summary>
public sealed class ProfileTerrainDialogResult
{
    public string TableName { get; init; } = string.Empty;
    public string TableType { get; init; } = "TCM_1";
    public double HorizontalDenom { get; init; } = 1000;
    public double VerticalDenom { get; init; } = 100;
    public double StartStation { get; init; }
    public double EndStation { get; init; }
    public double BaseElevation { get; init; }
    public double TopElevation { get; init; }
    public double StationInterval { get; init; } = 25;
    public ProfileTabulationMode TabulationMode { get; init; } = ProfileTabulationMode.CrossAxes;
    public double CrossAxisInterval { get; init; } = 20;
    public int BetweenDivisor { get; init; } = 2;
    public bool DrawTabulation { get; init; } = true;
    public bool DrawVerticals { get; init; } = true;
}
