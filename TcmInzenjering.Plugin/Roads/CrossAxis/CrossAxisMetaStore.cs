using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal enum CrossAxisOrigin
{
    Manual = 0,
    GeneratedEnd = 1
}

/// <summary>
/// Stacionaža i fiksno ime poprečne ose — preživljava pomeranje osovine.
/// </summary>
internal sealed class CrossAxisMeta
{
    public double Station { get; init; }
    public string? FixedName { get; init; }
    public string? RoadAxisName { get; init; }
    public int AxisNumber { get; init; }
    public double LeftWidth { get; init; }
    public double RightWidth { get; init; }
    public string Prefix { get; init; } = string.Empty;
    public CrossAxisOrigin Origin { get; init; } = CrossAxisOrigin.Manual;
    public bool HasFixedName => !string.IsNullOrWhiteSpace(FixedName);
}

internal static class CrossAxisMetaStore
{
    private const string DictionaryName = "TCM_CROSS_AXIS_SETTINGS";
    private const string KeyPrefix = "CXM_";

    public static void Save(
        Transaction tr,
        Database db,
        long handle,
        double station,
        string? roadAxisName,
        int axisNumber,
        double leftWidth,
        double rightWidth,
        string prefix,
        string? fixedName = null,
        CrossAxisOrigin origin = CrossAxisOrigin.Manual)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = KeyPrefix + handle;
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.Real, station),
            new TypedValue((int)DxfCode.Text, roadAxisName ?? string.Empty),
            new TypedValue((int)DxfCode.Text, fixedName ?? string.Empty),
            new TypedValue((int)DxfCode.Int32, axisNumber),
            new TypedValue((int)DxfCode.Real, leftWidth),
            new TypedValue((int)DxfCode.Real, rightWidth),
            new TypedValue((int)DxfCode.Text, prefix ?? string.Empty),
            new TypedValue((int)DxfCode.Int32, (int)origin));

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

    public static CrossAxisMeta? Load(Transaction tr, Database db, long handle)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var key = KeyPrefix + handle;
        if (!dictionary.Contains(key))
        {
            return null;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var items = record.Data?.AsArray();
        if (items is null || items.Length < 1)
        {
            return null;
        }

        var station = Convert.ToDouble(items[0].Value);
        var axisName = items.Length >= 2 ? items[1].Value?.ToString() ?? string.Empty : string.Empty;
        var fixedName = items.Length >= 3 ? items[2].Value?.ToString() ?? string.Empty : string.Empty;
        var axisNumber = items.Length >= 4 ? Convert.ToInt32(items[3].Value) : 0;
        var leftWidth = items.Length >= 5 ? Convert.ToDouble(items[4].Value) : 0;
        var rightWidth = items.Length >= 6 ? Convert.ToDouble(items[5].Value) : 0;
        var prefix = items.Length >= 7 ? items[6].Value?.ToString() ?? string.Empty : string.Empty;
        var origin = items.Length >= 8
            ? (CrossAxisOrigin)Convert.ToInt32(items[7].Value)
            : CrossAxisOrigin.Manual;
        return new CrossAxisMeta
        {
            Station = station,
            RoadAxisName = string.IsNullOrWhiteSpace(axisName) ? null : axisName,
            FixedName = string.IsNullOrWhiteSpace(fixedName) ? null : fixedName.Trim(),
            AxisNumber = axisNumber,
            LeftWidth = leftWidth,
            RightWidth = rightWidth,
            Prefix = prefix.Trim(),
            Origin = origin
        };
    }

    public static IReadOnlyList<(long Handle, CrossAxisMeta Meta)> LoadAllForRoadAxis(
        Transaction tr,
        Database db,
        string roadAxisName)
    {
        var list = new List<(long, CrossAxisMeta)>();
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (!entry.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!long.TryParse(entry.Key.Substring(KeyPrefix.Length), out var handle))
            {
                continue;
            }

            var record = (Xrecord)tr.GetObject(entry.Value, OpenMode.ForRead);
            var items = record.Data?.AsArray();
            if (items is null || items.Length < 1)
            {
                continue;
            }

            var station = Convert.ToDouble(items[0].Value);
            var axisName = items.Length >= 2 ? items[1].Value?.ToString() ?? string.Empty : string.Empty;
            if (!string.Equals(axisName, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fixedName = items.Length >= 3 ? items[2].Value?.ToString() ?? string.Empty : string.Empty;
            var axisNumber = items.Length >= 4 ? Convert.ToInt32(items[3].Value) : 0;
            var leftWidth = items.Length >= 5 ? Convert.ToDouble(items[4].Value) : 0;
            var rightWidth = items.Length >= 6 ? Convert.ToDouble(items[5].Value) : 0;
            var prefix = items.Length >= 7 ? items[6].Value?.ToString() ?? string.Empty : string.Empty;
            var origin = items.Length >= 8
                ? (CrossAxisOrigin)Convert.ToInt32(items[7].Value)
                : CrossAxisOrigin.Manual;
            list.Add((handle, new CrossAxisMeta
            {
                Station = station,
                RoadAxisName = axisName,
                FixedName = string.IsNullOrWhiteSpace(fixedName) ? null : fixedName.Trim(),
                AxisNumber = axisNumber,
                LeftWidth = leftWidth,
                RightWidth = rightWidth,
                Prefix = prefix.Trim(),
                Origin = origin
            }));
        }

        return list;
    }

    public static bool TryGetStation(Transaction tr, Database db, long handle, out double station)
    {
        station = 0;
        var meta = Load(tr, db, handle);
        if (meta is null)
        {
            return false;
        }

        station = meta.Station;
        return true;
    }

    public static void Remove(Transaction tr, Database db, long handle)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = KeyPrefix + handle;
        if (!dictionary.Contains(key))
        {
            return;
        }

        var id = dictionary.GetAt(key);
        dictionary.Remove(key);
        var record = (Xrecord)tr.GetObject(id, OpenMode.ForWrite);
        record.Erase();
    }

    /// <summary>
    /// Uklanja meta zapise čiji handle više ne postoji ili nije živa CAXIS entiteta.
    /// </summary>
    public static int PurgeInvalid(Transaction tr, Database db, string? roadAxisName = null)
    {
        var removed = 0;
        var all = string.IsNullOrWhiteSpace(roadAxisName)
            ? LoadAll(tr, db)
            : LoadAllForRoadAxis(tr, db, roadAxisName);

        foreach (var (handle, _) in all.ToList())
        {
            if (IsLiveCrossAxis(tr, db, handle, roadAxisName))
            {
                continue;
            }

            Remove(tr, db, handle);
            removed++;
        }

        return removed;
    }

    public static IReadOnlyList<(long Handle, CrossAxisMeta Meta)> LoadAll(Transaction tr, Database db)
    {
        var list = new List<(long, CrossAxisMeta)>();
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (!entry.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!long.TryParse(entry.Key.Substring(KeyPrefix.Length), out var handle))
            {
                continue;
            }

            var meta = Load(tr, db, handle);
            if (meta is not null)
            {
                list.Add((handle, meta));
            }
        }

        return list;
    }

    /// <summary>
    /// Jedinstvene ručne ose po stacionaži (posle purge-a).
    /// </summary>
    public static IReadOnlyList<CrossAxisMeta> LoadUniqueManualStations(
        Transaction tr,
        Database db,
        string roadAxisName)
    {
        const double tolerance = 1e-3;
        var unique = new List<CrossAxisMeta>();
        foreach (var (_, meta) in LoadAllForRoadAxis(tr, db, roadAxisName)
                     .Where(p => p.Meta.Origin == CrossAxisOrigin.Manual)
                     .OrderBy(p => p.Meta.Station))
        {
            if (unique.Any(u => Math.Abs(u.Station - meta.Station) <= tolerance))
            {
                continue;
            }

            unique.Add(meta);
        }

        return unique;
    }

    private static bool IsLiveCrossAxis(
        Transaction tr,
        Database db,
        long handle,
        string? roadAxisName)
    {
        try
        {
            var id = db.GetObjectId(false, new Handle(handle), 0);
            if (id.IsNull || id.IsErased)
            {
                return false;
            }

            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                return false;
            }

            if (!CrossAxisXData.TryReadCrossAxis(entity, out _, out var parent))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(roadAxisName) &&
                !string.IsNullOrWhiteSpace(parent) &&
                !string.Equals(parent, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static HashSet<string> LoadFixedNames(Transaction tr, Database db, string? roadAxisName = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (!entry.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var record = (Xrecord)tr.GetObject(entry.Value, OpenMode.ForRead);
            var items = record.Data?.AsArray();
            if (items is null || items.Length < 3)
            {
                continue;
            }

            var axis = items[1].Value?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(roadAxisName) &&
                !string.Equals(axis, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fixedName = items[2].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(fixedName))
            {
                names.Add(fixedName);
            }
        }

        return names;
    }

    private static DBDictionary GetDictionary(Transaction tr, Database db, OpenMode mode)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            if (mode == OpenMode.ForRead)
            {
                return new DBDictionary();
            }

            nod.UpgradeOpen();
            var dictionary = new DBDictionary();
            nod.SetAt(DictionaryName, dictionary);
            tr.AddNewlyCreatedDBObject(dictionary, true);
            return dictionary;
        }

        var existing = (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), mode);
        if (mode == OpenMode.ForWrite && !existing.IsWriteEnabled)
        {
            existing.UpgradeOpen();
        }

        return existing;
    }
}
