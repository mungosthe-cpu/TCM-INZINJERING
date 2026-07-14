using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal static class CrossAxisStore
{
    private const string DictionaryName = "TCM_CROSS_AXIS_SETTINGS";

    public static void Save(Transaction tr, Database db, long handle, CrossAxisPlacementSettings settings)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = GetKey(handle);
        var data = Serialize(settings);

        if (dictionary.Contains(key))
        {
            var existing = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
            existing.Data = data;
            return;
        }

        var record = new Xrecord { Data = data };
        dictionary.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    public static CrossAxisPlacementSettings Load(Transaction tr, Database db, long handle)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var key = GetKey(handle);
        if (!dictionary.Contains(key))
        {
            return new CrossAxisPlacementSettings();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        if (record.Data is null)
        {
            return new CrossAxisPlacementSettings();
        }

        return Deserialize(record.Data.AsArray());
    }

    private static ResultBuffer Serialize(CrossAxisPlacementSettings settings) =>
        new(
            new TypedValue((int)DxfCode.Real, settings.Labels.Enabled ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, (int)settings.Labels.Side),
            new TypedValue((int)DxfCode.Real, settings.Labels.OffsetX),
            new TypedValue((int)DxfCode.Real, settings.Labels.OffsetY),
            new TypedValue((int)DxfCode.Real, settings.Stations.Enabled ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, (int)settings.Stations.Side),
            new TypedValue((int)DxfCode.Real, settings.Stations.OffsetX),
            new TypedValue((int)DxfCode.Real, settings.Stations.OffsetY));

    private static CrossAxisPlacementSettings Deserialize(TypedValue[] items)
    {
        if (items.Length < 8)
        {
            return new CrossAxisPlacementSettings();
        }

        return new CrossAxisPlacementSettings
        {
            Labels = new CrossAxisOffsetSettings
            {
                Enabled = Convert.ToDouble(items[0].Value) > 0.5,
                Side = (CrossAxisSide)(int)Math.Round(Convert.ToDouble(items[1].Value)),
                OffsetX = Convert.ToDouble(items[2].Value),
                OffsetY = Convert.ToDouble(items[3].Value)
            },
            Stations = new CrossAxisOffsetSettings
            {
                Enabled = Convert.ToDouble(items[4].Value) > 0.5,
                Side = (CrossAxisSide)(int)Math.Round(Convert.ToDouble(items[5].Value)),
                OffsetX = Convert.ToDouble(items[6].Value),
                OffsetY = Convert.ToDouble(items[7].Value)
            }
        };
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

    private static string GetKey(long handle) => $"CX_{handle}";
}
