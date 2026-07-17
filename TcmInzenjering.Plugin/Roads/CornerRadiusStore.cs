using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Per-čvor (T1, T2…) radijusi zaobljenja — preživljavaju rebuild iz polilinije.
/// </summary>
internal static class CornerRadiusStore
{
    private const string DictionaryName = "TCM_INZINJERING_AXES";
    private const string KeyPrefix = "NODE_R_";

    public static IReadOnlyDictionary<int, double> Load(Transaction tr, Database db, string axisName)
    {
        if (!TryGetDictionary(tr, db, OpenMode.ForRead, out var dictionary) || dictionary is null)
        {
            return new Dictionary<int, double>();
        }

        var key = KeyPrefix + axisName;
        if (!dictionary.Contains(key))
        {
            return new Dictionary<int, double>();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length == 0)
        {
            return new Dictionary<int, double>();
        }

        var map = new Dictionary<int, double>();
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var node = Convert.ToInt32(data[i].Value);
            var radius = Convert.ToDouble(data[i + 1].Value);
            if (node > 0 && radius > 1e-6)
            {
                map[node] = radius;
            }
        }

        return map;
    }

    public static void Save(Transaction tr, Database db, string axisName, IReadOnlyDictionary<int, double> radii)
    {
        if (!TryGetDictionary(tr, db, OpenMode.ForWrite, out var dictionary) || dictionary is null)
        {
            return;
        }

        var key = KeyPrefix + axisName;
        var buffer = new ResultBuffer();
        foreach (var pair in radii.OrderBy(p => p.Key))
        {
            if (pair.Key <= 0 || pair.Value <= 1e-6)
            {
                continue;
            }

            buffer.Add(new TypedValue((int)DxfCode.Int16, (short)pair.Key));
            buffer.Add(new TypedValue((int)DxfCode.Real, pair.Value));
        }

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

    public static void SetNodeRadius(Transaction tr, Database db, string axisName, int nodeNumber, double radius)
    {
        var map = Load(tr, db, axisName).ToDictionary(p => p.Key, p => p.Value);
        map[nodeNumber] = radius;
        Save(tr, db, axisName, map);
    }

    public static double ResolveCornerRadius(
        IReadOnlyDictionary<int, double> nodeRadii,
        int cornerIndex1Based,
        double fallback)
    {
        if (nodeRadii.TryGetValue(cornerIndex1Based, out var r) && r > 1e-6)
        {
            return r;
        }

        return fallback;
    }

    private static bool TryGetDictionary(
        Transaction tr,
        Database db,
        OpenMode mode,
        out DBDictionary? dictionary)
    {
        dictionary = null;
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            if (mode == OpenMode.ForRead)
            {
                return false;
            }

            nod.UpgradeOpen();
            dictionary = new DBDictionary();
            nod.SetAt(DictionaryName, dictionary);
            tr.AddNewlyCreatedDBObject(dictionary, true);
            return true;
        }

        dictionary = (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), mode);
        if (mode == OpenMode.ForWrite && !dictionary.IsWriteEnabled)
        {
            dictionary.UpgradeOpen();
        }

        return true;
    }
}
