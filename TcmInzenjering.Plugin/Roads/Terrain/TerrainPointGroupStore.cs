using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Imenovane grupe izvornih tacaka terena (Tacke_1, Tacke_2...).</summary>
internal static class TerrainPointGroupStore
{
    private const string DictionaryName = "TCM_TEREN_POINT_GROUPS";

    public static string GetNextDefaultName(Transaction tr, Database db)
    {
        var names = ListNames(tr, db);
        var i = 1;
        while (names.Any(n => string.Equals(n, $"Tacke_{i}", StringComparison.OrdinalIgnoreCase)))
        {
            i++;
        }

        return $"Tacke_{i}";
    }

    public static IReadOnlyList<string> ListNames(Transaction tr, Database db)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            return Array.Empty<string>();
        }

        var dictionary = (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), OpenMode.ForRead);
        return dictionary.Cast<DBDictionaryEntry>()
            .Select(e => e.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void Save(
        Transaction tr,
        Database db,
        string groupName,
        IReadOnlyList<Point3d> points)
    {
        groupName = NormalizeName(groupName);
        if (points.Count == 0)
        {
            throw new InvalidOperationException("Grupa mora imati najmanje jednu tacku.");
        }

        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var buffer = new ResultBuffer();
        foreach (var p in points)
        {
            buffer.Add(new TypedValue((int)DxfCode.Real, p.X));
            buffer.Add(new TypedValue((int)DxfCode.Real, p.Y));
            buffer.Add(new TypedValue((int)DxfCode.Real, p.Z));
        }

        if (dictionary.Contains(groupName))
        {
            var existing = (Xrecord)tr.GetObject(dictionary.GetAt(groupName), OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        var record = new Xrecord { Data = buffer };
        dictionary.SetAt(groupName, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    public static void Rename(
        Transaction tr,
        Database db,
        string oldName,
        string newName,
        IReadOnlyList<Point3d> points)
    {
        oldName = NormalizeName(oldName);
        newName = NormalizeName(newName);
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            Save(tr, db, newName, points);
            return;
        }

        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        if (dictionary.Contains(newName))
        {
            throw new InvalidOperationException($"Grupa „{newName}“ vec postoji.");
        }

        Save(tr, db, newName, points);
        if (!dictionary.Contains(oldName))
        {
            return;
        }

        var oldRecord = (Xrecord)tr.GetObject(dictionary.GetAt(oldName), OpenMode.ForWrite);
        dictionary.Remove(oldName);
        oldRecord.Erase();
    }

    public static string NormalizeName(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("Unesite naziv grupe tacaka.");
        }

        var invalid = new HashSet<char>("\\/:*?\"<>|".ToCharArray());
        value = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return value.Length > 64 ? value[..64] : value;
    }

    private static DBDictionary GetDictionary(Transaction tr, Database db, OpenMode mode)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            if (mode != OpenMode.ForWrite)
            {
                throw new InvalidOperationException("Grupe tacaka ne postoje.");
            }

            nod.UpgradeOpen();
            var created = new DBDictionary();
            nod.SetAt(DictionaryName, created);
            tr.AddNewlyCreatedDBObject(created, true);
            return created;
        }

        return (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), mode);
    }
}
