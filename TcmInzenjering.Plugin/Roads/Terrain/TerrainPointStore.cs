using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Čuva privremeni skup 3D tačaka za formiranje terena (NOD / Xrecord).</summary>
internal static class TerrainPointStore
{
    private const string DictionaryName = "TCM_TEREN";
    private const string RecordKey = "PENDING_POINTS";

    public static void Save(Transaction tr, Database db, IReadOnlyList<Point3d> points)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var buffer = new ResultBuffer();
        foreach (var p in points)
        {
            buffer.Add(new TypedValue((int)DxfCode.Real, p.X));
            buffer.Add(new TypedValue((int)DxfCode.Real, p.Y));
            buffer.Add(new TypedValue((int)DxfCode.Real, p.Z));
        }

        if (dictionary.Contains(RecordKey))
        {
            var existing = (Xrecord)tr.GetObject(dictionary.GetAt(RecordKey), OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        var record = new Xrecord { Data = buffer };
        dictionary.SetAt(RecordKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    public static IReadOnlyList<Point3d> Load(Transaction tr, Database db)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        if (!dictionary.Contains(RecordKey))
        {
            return Array.Empty<Point3d>();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(RecordKey), OpenMode.ForRead);
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

    public static void Clear(Transaction tr, Database db)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        if (!dictionary.Contains(RecordKey))
        {
            return;
        }

        var id = dictionary.GetAt(RecordKey);
        var record = tr.GetObject(id, OpenMode.ForWrite);
        record.Erase();
    }

    /// <summary>Da li postoji sačuvan skup tačaka (bilo šta u Xrecord-u).</summary>
    public static bool HasPoints(Transaction tr, Database db)
    {
        return Load(tr, db).Count > 0;
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
