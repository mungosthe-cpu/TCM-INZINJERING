using System.IO;
using System.Text.Json;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>TCM projekat — globalni (svi crteži), grupiše teren / ose / podužni / tačke / granice.</summary>
public sealed class TcmProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Projekat 1";
    public List<string> TerrainNames { get; set; } = [];
    public List<string> AxisNames { get; set; } = [];
    public List<string> ProfileIds { get; set; } = [];
    public List<string> ProfileTitles { get; set; } = [];
    public List<string> PointSetNames { get; set; } = [];
    /// <summary>Ključevi granica: "Outer:handle" ili "Hide:handle".</summary>
    public List<string> BoundaryKeys { get; set; } = [];
    public string FolderPath { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Katalog projekata u %APPDATA%\TcmInzenjering\projects.json — isti u svim crtežima.
/// </summary>
internal static class TcmProjectStore
{
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string CatalogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "projects.json");

    private static readonly object Gate = new();
    private static CatalogDto? _cache;

    public static void Load()
    {
        lock (Gate)
        {
            _cache = ReadFile();
        }
    }

    public static IReadOnlyList<TcmProject> LoadAll()
    {
        lock (Gate)
        {
            EnsureCache();
            return NormalizeList(_cache!.Projects)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Kompatibilnost: ignoriše DWG, koristi globalni katalog.</summary>
    public static IReadOnlyList<TcmProject> LoadAll(Autodesk.AutoCAD.DatabaseServices.Transaction tr,
        Autodesk.AutoCAD.DatabaseServices.Database db)
    {
        _ = tr;
        _ = db;
        TryMigrateFromDrawing(tr, db);
        return LoadAll();
    }

    public static TcmProject? Load(string projectId)
    {
        return LoadAll().FirstOrDefault(p =>
            string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public static TcmProject? Load(
        Autodesk.AutoCAD.DatabaseServices.Transaction tr,
        Autodesk.AutoCAD.DatabaseServices.Database db,
        string projectId)
    {
        _ = tr;
        _ = db;
        return Load(projectId);
    }

    public static void Save(TcmProject project)
    {
        if (string.IsNullOrWhiteSpace(project.Id))
        {
            project.Id = Guid.NewGuid().ToString("N")[..8];
        }

        project.Name = string.IsNullOrWhiteSpace(project.Name) ? "Projekat" : project.Name.Trim();
        project.PointSetNames ??= [];
        project.BoundaryKeys ??= [];
        project.TerrainNames ??= [];
        project.AxisNames ??= [];
        project.ProfileIds ??= [];
        project.ProfileTitles ??= [];

        lock (Gate)
        {
            EnsureCache();
            var list = _cache!.Projects;
            var idx = list.FindIndex(p =>
                string.Equals(p.Id, project.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                list[idx] = project;
            }
            else
            {
                list.Add(project);
            }

            WriteFile(_cache);
        }
    }

    public static void Save(
        Autodesk.AutoCAD.DatabaseServices.Transaction tr,
        Autodesk.AutoCAD.DatabaseServices.Database db,
        TcmProject project)
    {
        _ = tr;
        _ = db;
        Save(project);
    }

    public static void Delete(string projectId)
    {
        lock (Gate)
        {
            EnsureCache();
            _cache!.Projects.RemoveAll(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_cache.ActiveId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                _cache.ActiveId = _cache.Projects.FirstOrDefault()?.Id ?? string.Empty;
            }

            WriteFile(_cache);
        }
    }

    public static void Delete(
        Autodesk.AutoCAD.DatabaseServices.Transaction tr,
        Autodesk.AutoCAD.DatabaseServices.Database db,
        string projectId)
    {
        _ = tr;
        _ = db;
        Delete(projectId);
    }

    public static string? GetActiveId()
    {
        lock (Gate)
        {
            EnsureCache();
            return string.IsNullOrWhiteSpace(_cache!.ActiveId) ? null : _cache.ActiveId;
        }
    }

    public static string? GetActiveId(
        Autodesk.AutoCAD.DatabaseServices.Transaction tr,
        Autodesk.AutoCAD.DatabaseServices.Database db)
    {
        _ = tr;
        _ = db;
        return GetActiveId();
    }

    public static void SetActiveId(string projectId)
    {
        lock (Gate)
        {
            EnsureCache();
            _cache!.ActiveId = projectId ?? string.Empty;
            WriteFile(_cache);
        }
    }

    public static void SetActiveId(
        Autodesk.AutoCAD.DatabaseServices.Transaction tr,
        Autodesk.AutoCAD.DatabaseServices.Database db,
        string projectId)
    {
        _ = tr;
        _ = db;
        SetActiveId(projectId);
    }

    /// <summary>
    /// Registruje imenovani skup tacaka u aktivni TCM projekat (bez duplikata).
    /// Vraca null pri uspehu, ili poruku o gresci.
    /// </summary>
    public static string? AddPointSetToActiveProject(string pointSetName)
    {
        var name = (pointSetName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unesite naziv skupa tacaka.";
        }

        var activeId = GetActiveId();
        if (string.IsNullOrWhiteSpace(activeId))
        {
            return "Nema aktivnog TCM projekta. Otvorite PROJEKAT i izaberite / kreirajte projekat.";
        }

        var project = Load(activeId);
        if (project is null)
        {
            return "Aktivni TCM projekat nije pronadjen.";
        }

        project.PointSetNames ??= [];
        if (!project.PointSetNames.Any(n =>
                string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            project.PointSetNames.Add(name);
            Save(project);
        }

        return null;
    }

    public static string FormatBoundaryKey(Terrain.TerrainBoundaryKind kind, long handle) =>
        $"{kind}:{handle}";

    public static bool TryParseBoundaryKey(string key, out Terrain.TerrainBoundaryKind kind, out long handle)
    {
        kind = Terrain.TerrainBoundaryKind.Outer;
        handle = 0;
        var parts = (key ?? string.Empty).Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!Enum.TryParse(parts[0], ignoreCase: true, out kind))
        {
            return false;
        }

        return long.TryParse(parts[1], out handle);
    }

    private static void TryMigrateFromDrawing(
        Autodesk.AutoCAD.DatabaseServices.Transaction tr,
        Autodesk.AutoCAD.DatabaseServices.Database db)
    {
        lock (Gate)
        {
            EnsureCache();
            if (_cache!.Projects.Count > 0)
            {
                return;
            }

            try
            {
                var nod = (Autodesk.AutoCAD.DatabaseServices.DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                if (!nod.Contains("TCM_PROJECTS"))
                {
                    return;
                }

                var dict = (Autodesk.AutoCAD.DatabaseServices.DBDictionary)tr.GetObject(
                    nod.GetAt("TCM_PROJECTS"), Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                if (!dict.Contains("PROJECT_INDEX"))
                {
                    return;
                }

                var index = (Autodesk.AutoCAD.DatabaseServices.Xrecord)tr.GetObject(
                    dict.GetAt("PROJECT_INDEX"), Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                var ids = new List<string>();
                foreach (var v in index.Data?.AsArray() ?? Array.Empty<Autodesk.AutoCAD.DatabaseServices.TypedValue>())
                {
                    var s = Convert.ToString(v.Value)?.Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        ids.Add(s);
                    }
                }

                foreach (var id in ids)
                {
                    var key = "PRJ_" + id;
                    if (!dict.Contains(key))
                    {
                        continue;
                    }

                    var rec = (Autodesk.AutoCAD.DatabaseServices.Xrecord)tr.GetObject(
                        dict.GetAt(key), Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    var json = Convert.ToString(rec.Data?.AsArray()?.FirstOrDefault().Value);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var project = JsonSerializer.Deserialize<TcmProject>(json, JsonOptions);
                    if (project is not null)
                    {
                        _cache.Projects.Add(Normalize(project));
                    }
                }

                if (dict.Contains("ACTIVE_PROJECT"))
                {
                    var act = (Autodesk.AutoCAD.DatabaseServices.Xrecord)tr.GetObject(
                        dict.GetAt("ACTIVE_PROJECT"), Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    _cache.ActiveId = Convert.ToString(act.Data?.AsArray()?.FirstOrDefault().Value) ?? string.Empty;
                }

                if (_cache.Projects.Count > 0)
                {
                    WriteFile(_cache);
                }
            }
            catch
            {
                // ignore migration errors
            }
        }
    }

    private static void EnsureCache()
    {
        _cache ??= ReadFile();
    }

    private static CatalogDto ReadFile()
    {
        try
        {
            if (!File.Exists(CatalogPath))
            {
                return new CatalogDto();
            }

            var dto = JsonSerializer.Deserialize<CatalogDto>(File.ReadAllText(CatalogPath), JsonOptions);
            if (dto is null)
            {
                return new CatalogDto();
            }

            dto.Projects = NormalizeList(dto.Projects);
            return dto;
        }
        catch
        {
            return new CatalogDto();
        }
    }

    private static void WriteFile(CatalogDto dto)
    {
        try
        {
            var dir = Path.GetDirectoryName(CatalogPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch
        {
            // best-effort
        }
    }

    private static List<TcmProject> NormalizeList(List<TcmProject>? list) =>
        (list ?? []).Select(Normalize).ToList();

    private static TcmProject Normalize(TcmProject p)
    {
        p.PointSetNames ??= [];
        p.BoundaryKeys ??= [];
        p.TerrainNames ??= [];
        p.AxisNames ??= [];
        p.ProfileIds ??= [];
        p.ProfileTitles ??= [];
        return p;
    }

    private sealed class CatalogDto
    {
        public string ActiveId { get; set; } = string.Empty;
        public List<TcmProject> Projects { get; set; } = [];
    }
}
