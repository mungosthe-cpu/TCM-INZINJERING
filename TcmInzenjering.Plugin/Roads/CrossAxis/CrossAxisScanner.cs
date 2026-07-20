using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal static class CrossAxisScanner
{
    public const string LayerName = "TCM_POPRECNA_OSA";

    public static IReadOnlyList<CrossAxisInfo> Scan(Transaction tr, Database db)
    {
        var ids = RoadEntityIndex.GetCrossAxes(tr, db);
        var axes = new List<CrossAxisInfo>();
        IEnumerable<ObjectId> source = ids.Count > 0
            ? ids
            : EnumerateModelSpace(tr, db);

        foreach (ObjectId id in source)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }
            if (!CrossAxisXData.TryReadCrossAxis(entity, out var number, out var parentAxis))
            {
                continue;
            }

            var meta = CrossAxisMetaStore.Load(tr, db, entity.Handle.Value);
            var station = meta?.Station ?? 0;
            if (station <= 0 &&
                RoadXData.TryReadStationLabel(entity, out _, out var role, out var tickStation) &&
                role == RoadXData.RoleTick)
            {
                station = tickStation;
            }

            axes.Add(new CrossAxisInfo
            {
                Number = number,
                Handle = entity.Handle.Value,
                Station = station,
                RoadAxisName = !string.IsNullOrWhiteSpace(meta?.RoadAxisName)
                    ? meta!.RoadAxisName!
                    : parentAxis
            });
        }

        return axes
            .OrderBy(axis => axis.Station)
            .ThenBy(axis => axis.Number)
            .ToList();
    }

    private static IEnumerable<ObjectId> EnumerateModelSpace(Transaction tr, Database db)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);
        foreach (ObjectId id in modelSpace)
        {
            yield return id;
        }
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
