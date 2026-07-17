using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

internal readonly record struct CornerCurveParams(double Radius, double L1, double L2);

/// <summary>
/// Per-čvor R + L1 + L2 (prelaznice) — preživljava rebuild.
/// </summary>
internal static class CornerCurveStore
{
    private const string DictionaryName = "TCM_INZINJERING_AXES";
    private const string KeyPrefix = "NODE_CURVE_";

    public static IReadOnlyDictionary<int, CornerCurveParams> Load(Transaction tr, Database db, string axisName)
    {
        if (!TryGetDictionary(tr, db, OpenMode.ForRead, out var dictionary) || dictionary is null)
        {
            return new Dictionary<int, CornerCurveParams>();
        }

        var key = KeyPrefix + axisName;
        if (!dictionary.Contains(key))
        {
            return new Dictionary<int, CornerCurveParams>();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length == 0)
        {
            return new Dictionary<int, CornerCurveParams>();
        }

        var map = new Dictionary<int, CornerCurveParams>();
        for (var i = 0; i + 3 < data.Length; i += 4)
        {
            var node = Convert.ToInt32(data[i].Value);
            var r = Convert.ToDouble(data[i + 1].Value);
            var l1 = Convert.ToDouble(data[i + 2].Value);
            var l2 = Convert.ToDouble(data[i + 3].Value);
            if (node > 0 && r > 1e-6)
            {
                map[node] = new CornerCurveParams(r, Math.Max(0, l1), Math.Max(0, l2));
            }
        }

        return map;
    }

    public static void Set(
        Transaction tr,
        Database db,
        string axisName,
        int nodeNumber,
        double radius,
        double l1,
        double l2)
    {
        var map = Load(tr, db, axisName).ToDictionary(p => p.Key, p => p.Value);
        map[nodeNumber] = new CornerCurveParams(radius, Math.Max(0, l1), Math.Max(0, l2));
        Save(tr, db, axisName, map);
        CornerRadiusStore.SetNodeRadius(tr, db, axisName, nodeNumber, radius);
    }

    public static void Save(
        Transaction tr,
        Database db,
        string axisName,
        IReadOnlyDictionary<int, CornerCurveParams> curves)
    {
        if (!TryGetDictionary(tr, db, OpenMode.ForWrite, out var dictionary) || dictionary is null)
        {
            return;
        }

        var key = KeyPrefix + axisName;
        var buffer = new ResultBuffer();
        foreach (var pair in curves.OrderBy(p => p.Key))
        {
            if (pair.Key <= 0 || pair.Value.Radius <= 1e-6)
            {
                continue;
            }

            buffer.Add(new TypedValue((int)DxfCode.Int16, (short)pair.Key));
            buffer.Add(new TypedValue((int)DxfCode.Real, pair.Value.Radius));
            buffer.Add(new TypedValue((int)DxfCode.Real, pair.Value.L1));
            buffer.Add(new TypedValue((int)DxfCode.Real, pair.Value.L2));
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
