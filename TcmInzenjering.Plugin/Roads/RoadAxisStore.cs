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
            new TypedValue((int)DxfCode.Real, metadata.LabelAtMainPoints ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Int64, metadata.SourcePolylineHandle),
            new TypedValue((int)DxfCode.Real, metadata.PolylineStartDistance),
            new TypedValue((int)DxfCode.Real, metadata.PolylineEndDistance),
            new TypedValue((int)DxfCode.Real, metadata.AxisCounterStart),
            new TypedValue((int)DxfCode.Real, (int)metadata.LabelFormat),
            new TypedValue((int)DxfCode.Real, metadata.DrawSegmentLabels ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, metadata.AxisColorIndex),
            new TypedValue((int)DxfCode.Real, metadata.StationTextColorIndex),
            new TypedValue((int)DxfCode.Real, metadata.StationTickColorIndex),
            new TypedValue((int)DxfCode.Real, metadata.SegmentLabelColorIndex),
            new TypedValue((int)DxfCode.Real, metadata.PolylineReferenceLength),
            new TypedValue((int)DxfCode.Real, metadata.ChainageFormat));

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
            LabelAtMainPoints = items.Length >= 14 && Convert.ToDouble(items[13].Value) > 0.5,
            SourcePolylineHandle = items.Length >= 15 ? Convert.ToInt64(items[14].Value) : 0,
            PolylineStartDistance = items.Length >= 16 ? Convert.ToDouble(items[15].Value) : startStation,
            PolylineEndDistance = items.Length >= 17 ? Convert.ToDouble(items[16].Value) : (items.Length >= 8 ? Convert.ToDouble(items[7].Value) : startStation),
            AxisCounterStart = items.Length >= 18 ? (int)Math.Round(Convert.ToDouble(items[17].Value)) : 1,
            LabelFormat = items.Length >= 19
                ? (StationLabelFormat)(int)Math.Round(Convert.ToDouble(items[18].Value))
                : StationLabelFormat.ProjectCounter,
            DrawSegmentLabels = items.Length >= 20 && Convert.ToDouble(items[19].Value) > 0.5,
            AxisColorIndex = items.Length >= 21 ? (short)Math.Round(Convert.ToDouble(items[20].Value)) : DrawingColorDefaults.Axis,
            StationTextColorIndex = items.Length >= 22 ? (short)Math.Round(Convert.ToDouble(items[21].Value)) : DrawingColorDefaults.StationText,
            StationTickColorIndex = items.Length >= 23 ? (short)Math.Round(Convert.ToDouble(items[22].Value)) : DrawingColorDefaults.StationTick,
            SegmentLabelColorIndex = items.Length >= 24 ? (short)Math.Round(Convert.ToDouble(items[23].Value)) : DrawingColorDefaults.SegmentLabel,
            PolylineReferenceLength = items.Length >= 25 ? Convert.ToDouble(items[24].Value) : 0,
            ChainageFormat = items.Length >= 26
                ? (int)Math.Round(Convert.ToDouble(items[25].Value))
                : ChainageFormatter.DefaultFormat
        };
    }

    public static IReadOnlyList<string> GetAxisNames(Transaction tr, Database db)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            return Array.Empty<string>();
        }

        var dictionary = (DBDictionary)tr.GetObject(
            nod.GetAt(DictionaryName), OpenMode.ForRead);
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

    public static bool Exists(Transaction tr, Database db, string axisName) =>
        GetAxisNames(tr, db).Any(name =>
            string.Equals(name, axisName?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string GetNextAvailableName(
        Transaction tr,
        Database db,
        string prefix = "OSA-")
    {
        var names = new HashSet<string>(
            GetAxisNames(tr, db),
            StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < int.MaxValue; index++)
        {
            var candidate = $"{prefix}{index}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{prefix}{Guid.NewGuid():N}";
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
