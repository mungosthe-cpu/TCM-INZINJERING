using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Imenovani tereni (Civil Surface by name) — tačke u NOD, aktivni teren, poziv po imenu.
/// </summary>
internal static class NamedTerrainSurfaceStore
{
    private const string DictionaryName = "TCM_TEREN";
    private const string IndexKey = "SURFACE_INDEX";
    private const string ActiveKey = "ACTIVE_SURFACE";
    private const string SurfacePrefix = "SURFACE_";

    public static IReadOnlyList<string> ListNames(Transaction tr, Database db)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        if (!dictionary.Contains(IndexKey))
        {
            return Array.Empty<string>();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(IndexKey), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length == 0)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var v in data)
        {
            if (v.TypeCode is (int)DxfCode.Text or (int)DxfCode.ExtendedDataAsciiString)
            {
                var name = Convert.ToString(v.Value)?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? GetActiveName(Transaction tr, Database db)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        if (!dictionary.Contains(ActiveKey))
        {
            return null;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(ActiveKey), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length == 0)
        {
            return null;
        }

        var name = Convert.ToString(data[0].Value)?.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static void SetActiveName(Transaction tr, Database db, string name)
    {
        name = NormalizeName(name);
        WriteRecord(tr, db, ActiveKey, new ResultBuffer(
            new TypedValue((int)DxfCode.Text, name)));
    }

    public static void SaveSurface(Transaction tr, Database db, string name, IReadOnlyList<Point3d> points) =>
        SaveSurface(tr, db, name, points, setActive: true);

    /// <summary>
    /// Snima imenovani skup tačaka. Ako <paramref name="setActive"/> = false (npr. *_Granica),
    /// ne menja aktivni teren ni radni TerrainPointStore.
    /// </summary>
    public static void SaveSurface(
        Transaction tr,
        Database db,
        string name,
        IReadOnlyList<Point3d> points,
        bool setActive)
    {
        name = NormalizeName(name);
        if (points.Count == 0)
        {
            throw new InvalidOperationException("Teren mora imati tacke.");
        }

        var buffer = new ResultBuffer();
        foreach (var p in points)
        {
            buffer.Add(new TypedValue((int)DxfCode.Real, p.X));
            buffer.Add(new TypedValue((int)DxfCode.Real, p.Y));
            buffer.Add(new TypedValue((int)DxfCode.Real, p.Z));
        }

        WriteRecord(tr, db, SurfaceKey(name), buffer);

        var names = ListNames(tr, db).ToList();
        if (!names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            names.Add(name);
        }
        else
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    names[i] = name;
                    break;
                }
            }
        }

        var indexBuffer = new ResultBuffer();
        foreach (var n in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            indexBuffer.Add(new TypedValue((int)DxfCode.Text, n));
        }

        WriteRecord(tr, db, IndexKey, indexBuffer);

        if (setActive)
        {
            SetActiveName(tr, db, name);
            TerrainPointStore.Save(tr, db, points);
        }
    }

    /// <summary>Ime pratećeg skupa tačaka granice: Teren_1 → Teren_1_Granica.</summary>
    public static string BoundaryCompanionName(string? surfaceName)
    {
        var baseName = string.IsNullOrWhiteSpace(surfaceName)
            ? "Teren_1"
            : NormalizeName(surfaceName);
        if (baseName.EndsWith("_Granica", StringComparison.OrdinalIgnoreCase))
        {
            return baseName;
        }

        return baseName + "_Granica";
    }

    public static bool IsBoundaryCompanionName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.EndsWith("_Granica", StringComparison.OrdinalIgnoreCase);

    /// <summary>Briše imenovani skup tačaka iz NOD (npr. Teren_1_Granica posle brisanja granice).</summary>
    public static bool DeleteSurface(Transaction tr, Database db, string name)
    {
        name = NormalizeName(name);
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = SurfaceKey(name);
        if (!dictionary.Contains(key))
        {
            var match = ListNames(tr, db)
                .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return false;
            }

            key = SurfaceKey(match);
            name = match;
            if (!dictionary.Contains(key))
            {
                return false;
            }
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
        dictionary.Remove(key);
        record.Erase();

        var names = ListNames(tr, db)
            .Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var indexBuffer = new ResultBuffer();
        foreach (var n in names)
        {
            indexBuffer.Add(new TypedValue((int)DxfCode.Text, n));
        }

        WriteRecord(tr, db, IndexKey, names.Count == 0 ? new ResultBuffer() : indexBuffer);

        var active = GetActiveName(tr, db);
        if (string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
        {
            if (names.Count > 0)
            {
                SetActiveName(tr, db, names[0]);
            }
            else if (dictionary.Contains(ActiveKey))
            {
                var act = (Xrecord)tr.GetObject(dictionary.GetAt(ActiveKey), OpenMode.ForWrite);
                dictionary.Remove(ActiveKey);
                act.Erase();
            }
        }

        return true;
    }

    public static IReadOnlyList<Point3d>? TryLoadSurface(Transaction tr, Database db, string name)
    {
        name = NormalizeName(name);
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var key = SurfaceKey(name);
        if (!dictionary.Contains(key))
        {
            // try case-insensitive match via index
            var match = ListNames(tr, db)
                .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return null;
            }

            key = SurfaceKey(match);
            if (!dictionary.Contains(key))
            {
                return null;
            }

            name = match;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length < 3)
        {
            return Array.Empty<Point3d>();
        }

        var points = new List<Point3d>(data.Length / 3);
        for (var i = 0; i + 2 < data.Length; i += 3)
        {
            if (data[i].TypeCode != (int)DxfCode.Real ||
                data[i + 1].TypeCode != (int)DxfCode.Real ||
                data[i + 2].TypeCode != (int)DxfCode.Real)
            {
                continue;
            }

            points.Add(new Point3d(
                Convert.ToDouble(data[i].Value),
                Convert.ToDouble(data[i + 1].Value),
                Convert.ToDouble(data[i + 2].Value)));
        }

        return points;
    }

    public static bool ActivateSurface(Transaction tr, Database db, string name, out IReadOnlyList<Point3d> points)
    {
        var loaded = TryLoadSurface(tr, db, name);
        if (loaded is null || loaded.Count == 0)
        {
            points = Array.Empty<Point3d>();
            return false;
        }

        var resolved = ListNames(tr, db)
                           .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                       ?? NormalizeName(name);
        TerrainPointStore.Save(tr, db, loaded);
        SetActiveName(tr, db, resolved);
        points = loaded;
        return true;
    }

    public static string NormalizeName(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
        {
            throw new ArgumentException("Ime terena je obavezno.");
        }

        if (n.Length > 64)
        {
            n = n[..64];
        }

        return n;
    }

    public static string SuggestNextName(Transaction tr, Database db)
    {
        var names = ListNames(tr, db);
        var active = GetActiveName(tr, db);
        if (!string.IsNullOrWhiteSpace(active))
        {
            return active;
        }

        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"Teren_{i}";
            if (!names.Any(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"Teren_{DateTime.Now:HHmmss}";
    }

    private static string SurfaceKey(string name)
    {
        var safe = new string(name.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "TEREN";
        }

        return SurfacePrefix + safe.ToUpperInvariant();
    }

    private static void WriteRecord(Transaction tr, Database db, string key, ResultBuffer buffer)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        if (dictionary.Contains(key))
        {
            var existing = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        var record = new Xrecord { Data = buffer };
        dictionary.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static DBDictionary GetDictionary(Transaction tr, Database db, OpenMode mode)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            if (mode != OpenMode.ForWrite)
            {
                return new DBDictionary();
            }

            nod.UpgradeOpen();
            var dictionary = new DBDictionary();
            nod.SetAt(DictionaryName, dictionary);
            tr.AddNewlyCreatedDBObject(dictionary, true);
            return dictionary;
        }

        return (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), mode);
    }
}
