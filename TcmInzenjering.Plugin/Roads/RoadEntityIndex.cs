using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Keš ObjectId-jeva po putnoj osi / sloju — izbegava ponavljane full model-space skenove.
/// Invalidira se posle brisanja/crtanja ili eksplicitno.
/// </summary>
internal static class RoadEntityIndex
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<ObjectId>> ByKey = new(StringComparer.OrdinalIgnoreCase);
    private static bool _dirty = true;

    public static void Invalidate()
    {
        lock (Gate)
        {
            _dirty = true;
            ByKey.Clear();
        }
    }

    public static IReadOnlyList<ObjectId> GetForAxisLayer(
        Transaction tr,
        Database db,
        string axisName,
        string layerName)
    {
        var key = $"{axisName}|{layerName}";
        lock (Gate)
        {
            if (!_dirty && ByKey.TryGetValue(key, out var cached))
            {
                return FilterLive(cached);
            }
        }

        Rebuild(tr, db);

        lock (Gate)
        {
            return ByKey.TryGetValue(key, out var list)
                ? FilterLive(list)
                : Array.Empty<ObjectId>();
        }
    }

    public static IReadOnlyList<ObjectId> GetCrossAxes(Transaction tr, Database db, string? roadAxisName = null)
    {
        const string keyAll = "*|CROSS";
        lock (Gate)
        {
            if (_dirty)
            {
                // fall through to rebuild below
            }
            else if (roadAxisName is null && ByKey.TryGetValue(keyAll, out var all))
            {
                return FilterLive(all);
            }
            else if (roadAxisName is not null &&
                     ByKey.TryGetValue($"{roadAxisName}|CROSS", out var byAxis))
            {
                return FilterLive(byAxis);
            }
        }

        Rebuild(tr, db);

        lock (Gate)
        {
            if (roadAxisName is null)
            {
                return ByKey.TryGetValue(keyAll, out var all)
                    ? FilterLive(all)
                    : Array.Empty<ObjectId>();
            }

            return ByKey.TryGetValue($"{roadAxisName}|CROSS", out var list)
                ? FilterLive(list)
                : Array.Empty<ObjectId>();
        }
    }

    private static void Rebuild(Transaction tr, Database db)
    {
        var map = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                continue;
            }

            if (RoadXData.TryReadAxisElement(entity, out var axisName, out _))
            {
                Add(map, $"{axisName}|{entity.Layer}", id);
            }
            else if (RoadXData.TryReadStationLabel(entity, out axisName, out _, out _))
            {
                Add(map, $"{axisName}|{entity.Layer}", id);
            }
            else if (RoadXData.TryReadRadiusAnnotation(entity, out axisName, out _))
            {
                Add(map, $"{axisName}|{entity.Layer}", id);
            }
            else if (RoadXData.TryReadSourcePolyline(entity, out axisName))
            {
                Add(map, $"{axisName}|{entity.Layer}", id);
            }
            else if (CrossAxis.CrossAxisXData.TryReadCrossAxis(entity, out _, out var parent))
            {
                Add(map, "*|CROSS", id);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Add(map, $"{parent}|CROSS", id);
                }
            }
        }

        lock (Gate)
        {
            ByKey.Clear();
            foreach (var kv in map)
            {
                ByKey[kv.Key] = kv.Value;
            }

            _dirty = false;
        }
    }

    private static void Add(Dictionary<string, List<ObjectId>> map, string key, ObjectId id)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<ObjectId>();
            map[key] = list;
        }

        list.Add(id);
    }

    private static IReadOnlyList<ObjectId> FilterLive(List<ObjectId> ids)
    {
        var live = ids.Where(id => !id.IsNull && !id.IsErased).ToList();
        return live;
    }
}
