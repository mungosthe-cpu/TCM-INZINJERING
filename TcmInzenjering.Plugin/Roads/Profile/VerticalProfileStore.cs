using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Projektovana niveleta (VA): PVI tačke (stacionaža, kota) po putnoj osi.
/// Linearna interpolacija između PVI; vertikalne krivine se mogu dodati kasnije.
/// </summary>
internal sealed class VerticalPvi
{
    public double Station { get; init; }
    public double Elevation { get; init; }
}

internal static class VerticalProfileStore
{
    private const string DictionaryName = "TCM_VERTICAL_PROFILE";
    private const string KeyPrefix = "VA_";

    public static void Save(Transaction tr, Database db, string axisName, IReadOnlyList<VerticalPvi> pvis)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = KeyPrefix + axisName.Trim().ToUpperInvariant();
        var values = new List<TypedValue>
        {
            new((int)DxfCode.Text, axisName.Trim())
        };

        foreach (var p in pvis.OrderBy(p => p.Station))
        {
            values.Add(new TypedValue((int)DxfCode.Real, p.Station));
            values.Add(new TypedValue((int)DxfCode.Real, p.Elevation));
        }

        var buffer = new ResultBuffer(values.ToArray());
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

    public static IReadOnlyList<VerticalPvi> Load(Transaction tr, Database db, string axisName)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var key = KeyPrefix + axisName.Trim().ToUpperInvariant();
        if (!dictionary.Contains(key))
        {
            return Array.Empty<VerticalPvi>();
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var items = record.Data?.AsArray();
        if (items is null || items.Length < 3)
        {
            return Array.Empty<VerticalPvi>();
        }

        var list = new List<VerticalPvi>();
        for (var i = 1; i + 1 < items.Length; i += 2)
        {
            if (items[i].TypeCode != (int)DxfCode.Real || items[i + 1].TypeCode != (int)DxfCode.Real)
            {
                continue;
            }

            list.Add(new VerticalPvi
            {
                Station = Convert.ToDouble(items[i].Value),
                Elevation = Convert.ToDouble(items[i + 1].Value)
            });
        }

        return list.OrderBy(p => p.Station).ToList();
    }

    public static bool HasProfile(Transaction tr, Database db, string axisName) =>
        Load(tr, db, axisName).Count >= 2;

    public static void Remove(Transaction tr, Database db, string axisName)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = KeyPrefix + axisName.Trim().ToUpperInvariant();
        if (!dictionary.Contains(key))
        {
            return;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
        dictionary.Remove(key);
        record.Erase();
    }

    /// <summary>Uzorkovanje nivelete na stacionaži (linearno između PVI).</summary>
    public static double? ElevationAt(IReadOnlyList<VerticalPvi> pvis, double station)
    {
        if (pvis.Count == 0)
        {
            return null;
        }

        if (pvis.Count == 1)
        {
            return pvis[0].Elevation;
        }

        if (station <= pvis[0].Station)
        {
            return pvis[0].Elevation;
        }

        if (station >= pvis[^1].Station)
        {
            return pvis[^1].Elevation;
        }

        for (var i = 0; i < pvis.Count - 1; i++)
        {
            var a = pvis[i];
            var b = pvis[i + 1];
            if (station < a.Station - 1e-9 || station > b.Station + 1e-9)
            {
                continue;
            }

            var span = b.Station - a.Station;
            if (Math.Abs(span) < 1e-9)
            {
                return a.Elevation;
            }

            var t = (station - a.Station) / span;
            return a.Elevation + t * (b.Elevation - a.Elevation);
        }

        return null;
    }

    public static List<(double Station, double Elevation)> SampleDense(
        IReadOnlyList<VerticalPvi> pvis,
        double startStation,
        double endStation,
        double step = 5.0)
    {
        var result = new List<(double, double)>();
        if (pvis.Count < 2)
        {
            return result;
        }

        step = Math.Max(0.5, step);
        // Uključi sve PVI u opsegu + regularni korak.
        var stations = new SortedSet<double>();
        for (var s = startStation; s <= endStation + 1e-6; s += step)
        {
            stations.Add(s);
        }

        stations.Add(startStation);
        stations.Add(endStation);
        foreach (var p in pvis)
        {
            if (p.Station >= startStation - 1e-6 && p.Station <= endStation + 1e-6)
            {
                stations.Add(p.Station);
            }
        }

        foreach (var s in stations)
        {
            var z = ElevationAt(pvis, s);
            if (z is not null)
            {
                result.Add((s, z.Value));
            }
        }

        return result;
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
