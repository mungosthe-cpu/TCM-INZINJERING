using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Čuva kriterijume projekcije po osi (način, broj tačaka, dužina pri definisanju, handle-i terena).
/// </summary>
internal static class TerrainProjectionStore
{
    private const string DictionaryName = "TCM_PROJ_AXIS";

    public static void Save(
        Transaction tr,
        Database db,
        string axisName,
        TerrainSamplingOptions options,
        double referenceLength,
        IReadOnlyList<ObjectId> terrainIds)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.Text, axisName),
            new TypedValue((int)DxfCode.Int16, (short)options.Mode),
            new TypedValue((int)DxfCode.Int32, options.PointCount),
            new TypedValue((int)DxfCode.Real, Math.Max(1e-6, referenceLength)));

        foreach (var id in terrainIds)
        {
            if (id.IsNull)
            {
                continue;
            }

            buffer.Add(new TypedValue((int)DxfCode.Text, id.Handle.ToString()));
        }

        Xrecord record;
        if (dictionary.Contains(axisName))
        {
            record = (Xrecord)tr.GetObject(dictionary.GetAt(axisName), OpenMode.ForWrite);
            record.Data = buffer;
        }
        else
        {
            record = new Xrecord { Data = buffer };
            dictionary.SetAt(axisName, record);
            tr.AddNewlyCreatedDBObject(record, true);
        }
    }

    public static bool TryLoad(
        Transaction tr,
        Database db,
        string axisName,
        out TerrainSamplingOptions options,
        out double referenceLength,
        out List<ObjectId> terrainIds)
    {
        options = new TerrainSamplingOptions();
        referenceLength = 0;
        terrainIds = new List<ObjectId>();

        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        if (!dictionary.Contains(axisName))
        {
            return false;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(axisName), OpenMode.ForRead);
        var data = record.Data?.AsArray();
        if (data is null || data.Length < 3)
        {
            return false;
        }

        var mode = (TerrainSamplingMode)Convert.ToInt16(data[1].Value);
        var pointCount = Math.Max(2, Convert.ToInt32(data[2].Value));
        options = new TerrainSamplingOptions
        {
            Mode = mode is TerrainSamplingMode.TerrainEdgeCrossings or TerrainSamplingMode.FixedPointCount
                ? mode
                : TerrainSamplingMode.FixedPointCount,
            PointCount = pointCount
        };

        var handleStart = 3;
        if (data.Length >= 4 && data[3].TypeCode == (int)DxfCode.Real)
        {
            referenceLength = Convert.ToDouble(data[3].Value);
            handleStart = 4;
        }

        for (var i = handleStart; i < data.Length; i++)
        {
            var handleText = data[i].Value?.ToString();
            if (string.IsNullOrWhiteSpace(handleText) ||
                string.Equals(handleText, axisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var handle = new Handle(Convert.ToInt64(handleText, 16));
                if (db.TryGetObjectId(handle, out var id) && !id.IsNull && !id.IsErased)
                {
                    terrainIds.Add(id);
                }
            }
            catch
            {
                // Nevalidan handle — preskoči.
            }
        }

        return terrainIds.Count > 0;
    }

    /// <summary>
    /// Broj tačaka srazmerno novoj dužini ose (u odnosu na dužinu pri definisanju projekcije).
    /// </summary>
    public static int ScalePointCount(int definedPointCount, double referenceLength, double currentLength)
    {
        var n = Math.Max(2, definedPointCount);
        if (referenceLength < 1e-6 || currentLength < 1e-9)
        {
            return n;
        }

        return Math.Max(2, (int)Math.Round(n * (currentLength / referenceLength)));
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

        return (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), mode);
    }
}
