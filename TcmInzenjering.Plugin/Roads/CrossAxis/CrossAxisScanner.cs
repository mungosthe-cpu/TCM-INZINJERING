using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal static class CrossAxisScanner
{
    public const string LayerName = "TCM_POPRECNA_OSA";

    public static IReadOnlyList<CrossAxisInfo> Scan(Transaction tr, Database db)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        var axes = new List<CrossAxisInfo>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }
            if (!CrossAxisXData.TryReadCrossAxis(entity, out var number))
            {
                continue;
            }

            axes.Add(new CrossAxisInfo
            {
                Number = number,
                Handle = entity.Handle.Value
            });
        }

        return axes
            .OrderBy(axis => axis.Number)
            .ToList();
    }

    public static int GetNextNumber(Transaction tr, Database db)
    {
        var axes = Scan(tr, db);
        return axes.Count == 0 ? 1 : axes[^1].Number + 1;
    }

    public static bool TryGetEntity(Transaction tr, Database db, long handle, out Entity entity)
    {
        entity = null!;
        try
        {
            var id = db.GetObjectId(false, new Handle(handle), 0);
            if (id.IsNull)
            {
                return false;
            }

            entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            return CrossAxisXData.TryReadCrossAxis(entity, out _);
        }
        catch
        {
            return false;
        }
    }
}
