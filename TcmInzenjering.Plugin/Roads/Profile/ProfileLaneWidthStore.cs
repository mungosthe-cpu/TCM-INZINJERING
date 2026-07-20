using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Širine kolovoza levo/desno po osi (podrazumevano + opcione tačke po stacionaži).
/// </summary>
internal static class ProfileLaneWidthStore
{
    private const string DictionaryName = "TCM_LANE_WIDTHS";
    private const string KeyPrefix = "LW_";

    public static void SaveDefaults(
        Transaction tr,
        Database db,
        string axisName,
        double leftWidth,
        double rightWidth)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = KeyPrefix + axisName.Trim().ToUpperInvariant();
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.Text, axisName.Trim()),
            new TypedValue((int)DxfCode.Real, Math.Max(0, leftWidth)),
            new TypedValue((int)DxfCode.Real, Math.Max(0, rightWidth)));

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

    public static bool TryGetDefaults(
        Transaction tr,
        Database db,
        string axisName,
        out double leftWidth,
        out double rightWidth)
    {
        leftWidth = 0;
        rightWidth = 0;
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var key = KeyPrefix + axisName.Trim().ToUpperInvariant();
        if (!dictionary.Contains(key))
        {
            return false;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var items = record.Data?.AsArray();
        if (items is null || items.Length < 3)
        {
            return false;
        }

        leftWidth = Convert.ToDouble(items[1].Value);
        rightWidth = Convert.ToDouble(items[2].Value);
        return leftWidth > 1e-9 || rightWidth > 1e-9;
    }

    public static bool TryGetAtStation(
        string axisName,
        double station,
        out double leftWidth,
        out double rightWidth)
    {
        leftWidth = 0;
        rightWidth = 0;
        try
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc is null)
            {
                return false;
            }

            using var tr = doc.Database.TransactionManager.StartTransaction();
            var ok = TryGetAtStation(tr, doc.Database, axisName, station, out leftWidth, out rightWidth);
            tr.Commit();
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetAtStation(
        Transaction tr,
        Database db,
        string axisName,
        double station,
        out double leftWidth,
        out double rightWidth) =>
        LaneWidthEvaluator.TryGetLdAtStation(
            tr, db, axisName, station, out leftWidth, out rightWidth);

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

/// <summary>Kote projektovane nivelete iz <see cref="VerticalProfileStore"/>.</summary>
internal static class ProfileGradeSampler
{
    public static bool TryLoadSamples(
        Transaction? tr,
        Database? db,
        string axisName,
        out List<(double Station, double Elevation)> samples)
    {
        samples = [];
        if (tr is null || db is null || string.IsNullOrWhiteSpace(axisName))
        {
            return false;
        }

        var pvis = VerticalProfileStore.Load(tr, db, axisName);
        if (pvis.Count < 2)
        {
            return false;
        }

        var start = pvis[0].Station;
        var end = pvis[^1].Station;
        samples = VerticalProfileStore.SampleDense(pvis, start, end, step: 2.0);
        return samples.Count >= 2;
    }
}
