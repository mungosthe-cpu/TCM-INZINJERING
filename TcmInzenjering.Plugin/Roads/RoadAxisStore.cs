using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

internal static class RoadAxisStore
{
    private const string DictionaryName = "TCM_INZINJERING_AXES";

    public static void Save(Transaction tr, Database db, RoadAxisMetadata metadata)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = GetKey(metadata.Name);
        var data = new ResultBuffer(
            new TypedValue((int)DxfCode.Real, metadata.StartStation),
            new TypedValue((int)DxfCode.Real, metadata.Interval),
            new TypedValue((int)DxfCode.Real, metadata.TickLength),
            new TypedValue((int)DxfCode.Real, metadata.TextHeight),
            new TypedValue((int)DxfCode.Text, metadata.Prefix),
            new TypedValue((int)DxfCode.Real, metadata.LabelSideSign),
            new TypedValue((int)DxfCode.Real, metadata.CurveRadius),
            new TypedValue((int)DxfCode.Real, metadata.EndStation),
            new TypedValue((int)DxfCode.Real, metadata.EqualIntervalInBounds ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, metadata.WholeInterval ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, metadata.AlignToStart ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, metadata.LabelAtStart ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, metadata.LabelAtEnd ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, metadata.LabelAtMainPoints ? 1.0 : 0.0));

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

    public static RoadAxisMetadata? Load(Transaction tr, Database db, string axisName)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var key = GetKey(axisName);
        if (!dictionary.Contains(key))
        {
            return null;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        if (record.Data is null)
        {
            return null;
        }

        var items = record.Data.AsArray();
        if (items.Length < 6)
        {
            return null;
        }

        var startStation = Convert.ToDouble(items[0].Value);
        var interval = Convert.ToDouble(items[1].Value);

        return new RoadAxisMetadata
        {
            Name = axisName,
            StartStation = startStation,
            Interval = interval,
            TickLength = Convert.ToDouble(items[2].Value),
            TextHeight = Convert.ToDouble(items[3].Value),
            Prefix = items[4].Value?.ToString() ?? "STA ",
            LabelSideSign = Convert.ToDouble(items[5].Value),
            CurveRadius = items.Length >= 7 ? Convert.ToDouble(items[6].Value) : 50.0,
            EndStation = items.Length >= 8 ? Convert.ToDouble(items[7].Value) : startStation,
            EqualIntervalInBounds = items.Length < 9 || Convert.ToDouble(items[8].Value) > 0.5,
            WholeInterval = items.Length < 10 || Convert.ToDouble(items[9].Value) > 0.5,
            AlignToStart = items.Length < 11 || Convert.ToDouble(items[10].Value) > 0.5,
            LabelAtStart = items.Length >= 12 && Convert.ToDouble(items[11].Value) > 0.5,
            LabelAtEnd = items.Length < 13 || Convert.ToDouble(items[12].Value) > 0.5,
            LabelAtMainPoints = items.Length >= 14 && Convert.ToDouble(items[13].Value) > 0.5
        };
    }

    public static IReadOnlyList<string> GetAxisNames(Transaction tr, Database db)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var names = new List<string>();
        foreach (var entry in dictionary)
        {
            if (entry.Key.StartsWith("AXIS_", StringComparison.Ordinal))
            {
                names.Add(entry.Key["AXIS_".Length..]);
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
                throw new InvalidOperationException("Nema sacuvanih osa.");
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

    private static string GetKey(string axisName) => $"AXIS_{axisName}";
}
