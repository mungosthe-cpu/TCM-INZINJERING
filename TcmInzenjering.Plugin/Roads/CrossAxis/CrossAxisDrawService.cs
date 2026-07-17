using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Roads.CrossAxis;

namespace TcmInzenjering.Plugin.Roads;

public sealed class CrossAxisDrawParameters
{
    public double Station { get; set; }
    public double LeftWidth { get; set; } = 30;
    public double RightWidth { get; set; } = 30;
    public bool AutoNaming { get; set; } = true;
    public string Prefix { get; set; } = "STA ";
    public int CounterStart { get; set; } = 1;
    public bool IncreasingNumbers { get; set; } = true;
    public string FixedName { get; set; } = string.Empty;
}

internal static class CrossAxisDrawService
{
    private const double StationTolerance = 1e-3;

    public static bool TryDrawAtStation(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        string roadAxisName,
        RoadAxis axis,
        RoadAxisMetadata metadata,
        CrossAxisDrawParameters parameters,
        out string? error)
    {
        error = null;
        var station = parameters.Station;
        var axisEnd = axis.Elements[^1].EndStation;
        if (station < axis.StartStation - StationTolerance || station > axisEnd + StationTolerance)
        {
            error = $"Stacionaža {station:0.###} je van opsega osovine ({axis.StartStation:0.###} – {axisEnd:0.###}).";
            return false;
        }

        station = Math.Max(axis.StartStation, Math.Min(station, axisEnd));
        var options = metadata.ToLabelOptions();

        if (!parameters.AutoNaming)
        {
            var fixedName = parameters.FixedName.Trim();
            if (string.IsNullOrWhiteSpace(fixedName))
            {
                error = "Unesite fiksno ime poprečne ose.";
                return false;
            }

            if (CrossAxisMetaStore.LoadFixedNames(tr, db, roadAxisName).Contains(fixedName))
            {
                error = $"Poprečna osa sa imenom „{fixedName}“ već postoji.";
                return false;
            }

            if (!DrawFixedAxis(
                    tr, db, modelSpace, roadAxisName, axis, metadata, options,
                    station, fixedName, parameters.LeftWidth, parameters.RightWidth, out error))
            {
                return false;
            }

            SynchronizeNumbering(tr, db, roadAxisName, metadata, options, parameters);
            return true;
        }

        if (HasManualCrossAxisAtStation(tr, db, roadAxisName, station))
        {
            error = $"Na stacionaži {station:0.###} već postoji poprečna osa.";
            return false;
        }

        // Privremeni broj — konačan se dodeljuje u SynchronizeNumbering po redosledu stacionaže.
        var provisional = Math.Max(1, parameters.CounterStart);
        if (!DrawNumberedAxis(
                tr, db, modelSpace, roadAxisName, axis, metadata, options,
                station, provisional, parameters, out error))
        {
            return false;
        }

        SynchronizeNumbering(tr, db, roadAxisName, metadata, options, parameters);
        return true;
    }

    /// <summary>
    /// Briše poprečne ose (štapić + oznake + meta), renumeriše preostale i vraća pogođene osovine.
    /// </summary>
    public static IReadOnlyList<string> DeleteByHandles(
        Transaction tr,
        Database db,
        IReadOnlyCollection<long> handles)
    {
        var affectedAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in handles.Distinct())
        {
            ObjectId id;
            try
            {
                id = db.GetObjectId(false, new Handle(handle), 0);
            }
            catch
            {
                continue;
            }

            if (id.IsNull || id.IsErased)
            {
                CrossAxisMetaStore.Remove(tr, db, handle);
                CrossAxisStore.Remove(tr, db, handle);
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                CrossAxisMetaStore.Remove(tr, db, handle);
                CrossAxisStore.Remove(tr, db, handle);
                continue;
            }

            var meta = CrossAxisMetaStore.Load(tr, db, handle);
            var roadAxisName = meta?.RoadAxisName ?? string.Empty;
            double? station = meta is not null ? meta.Station : null;
            var axisNumber = meta?.AxisNumber ?? 0;

            if (CrossAxisXData.TryReadCrossAxis(entity, out var caxisNumber, out var parent))
            {
                if (caxisNumber > 0)
                {
                    axisNumber = caxisNumber;
                }

                if (!string.IsNullOrWhiteSpace(parent))
                {
                    roadAxisName = parent;
                }
            }

            if (RoadXData.TryReadStationLabel(entity, out var tickAxis, out var role, out var tickStation) &&
                role == RoadXData.RoleTick)
            {
                if (string.IsNullOrWhiteSpace(roadAxisName))
                {
                    roadAxisName = tickAxis;
                }

                station ??= tickStation;
            }

            Point3d? origin = null;
            if (CrossAxisGeometry.TryGetFrame(entity, out var frameOrigin, out _, out _))
            {
                origin = frameOrigin;
            }

            EraseLabelsForDeletedAxis(tr, db, roadAxisName, station, axisNumber, origin);

            if (!entity.IsWriteEnabled)
            {
                entity.UpgradeOpen();
            }

            if (!entity.IsErased)
            {
                entity.Erase();
            }

            CrossAxisMetaStore.Remove(tr, db, handle);
            CrossAxisStore.Remove(tr, db, handle);

            if (!string.IsNullOrWhiteSpace(roadAxisName))
            {
                affectedAxes.Add(roadAxisName);
            }
        }

        foreach (var axisName in affectedAxes)
        {
            var metadata = RoadAxisStore.Load(tr, db, axisName);
            if (metadata is null)
            {
                continue;
            }

            SynchronizeNumbering(
                tr, db, axisName, metadata, metadata.ToLabelOptions(),
                parameters: null,
                createMissingLabels: false);
        }

        return affectedAxes.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void EraseLabelsForDeletedAxis(
        Transaction tr,
        Database db,
        string roadAxisName,
        double? station,
        int axisNumber,
        Point3d? origin)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        // Samo veoma blizu štapića — ne diraj susedne stacionaže (interval ~20 m).
        const double orphanProximity = 12.0;
        var toErase = new List<ObjectId>();

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            // CXLB / CXST vezani za STA N.
            if (axisNumber > 0 &&
                CrossAxisXData.TryReadCrossAnnotation(entity, out _, out var linkedNumber, out var annParent) &&
                linkedNumber == axisNumber)
            {
                if (string.IsNullOrWhiteSpace(roadAxisName) ||
                    string.IsNullOrWhiteSpace(annParent) ||
                    string.Equals(annParent, roadAxisName, StringComparison.OrdinalIgnoreCase))
                {
                    toErase.Add(id);
                    continue;
                }
            }

            if (RoadXData.TryReadStationLabel(entity, out var name, out var role, out var labelStation))
            {
                if (role != RoadXData.RoleText && role != RoadXData.RoleChainage)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(roadAxisName) &&
                    !string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Samo natpisi TAČNO na stacionaži obrisane ose — ne susedi.
                if (station is not null && Math.Abs(labelStation - station.Value) <= StationTolerance)
                {
                    toErase.Add(id);
                }

                continue;
            }

            // Orphan tekst bez XData (retko) — samo uz sam štapić.
            if (origin is not null &&
                entity is DBText or MText &&
                GetEntityPosition(entity).DistanceTo(origin.Value) <= orphanProximity &&
                LooksLikeCrossAxisLabel(entity))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase.Distinct())
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            if (!entity.IsErased)
            {
                entity.Erase();
            }
        }
    }

    private static bool LooksLikeCrossAxisLabel(Entity entity)
    {
        string text;
        if (entity is DBText dbText)
        {
            text = dbText.TextString ?? string.Empty;
        }
        else if (entity is MText mText)
        {
            text = mText.Contents ?? string.Empty;
        }
        else
        {
            return false;
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        return text.IndexOf("STA", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf('+') >= 0 ||
               text.IndexOf('-') >= 0;
    }

    private static void EraseAllLabelsAtStation(
        Transaction tr,
        Database db,
        string roadAxisName,
        double station)
    {
        EraseLabelsForDeletedAxis(tr, db, roadAxisName, station, axisNumber: 0, origin: null);
    }

    /// <summary>
    /// Sve poprečne ose (interval + ručne): sortiraj po stacionaži, dodeli STA N, N+1, …
    /// Jedan prolaz kroz model space — bez ponavljanja skeniranja po tick-u.
    /// </summary>
    public static void SynchronizeNumbering(
        Transaction tr,
        Database db,
        string roadAxisName,
        RoadAxisMetadata metadata,
        StationLabelOptions options,
        CrossAxisDrawParameters? parameters = null,
        bool createMissingLabels = true)
    {
        CrossAxisMetaStore.PurgeInvalid(tr, db, roadAxisName);

        var counterStart = parameters?.CounterStart > 0
            ? parameters.CounterStart
            : Math.Max(0, options.AxisCounterStart);
        var increasing = parameters?.IncreasingNumbers ?? true;
        var prefix = !string.IsNullOrWhiteSpace(parameters?.Prefix)
            ? parameters!.Prefix
            : metadata.Prefix;

        // Jedan prolaz: tick-ovi + RoleText/RoleChainage za ovu osovinu.
        CollectAxisStationEntities(
            tr, db, roadAxisName,
            out var ticks,
            out var textsByStation,
            out var chainagesByStation);

        if (ticks.Count == 0)
        {
            return;
        }

        // Dedup tick-ova na istoj stacionaži (zadrži CAXIS).
        ticks = DeduplicateTicksInMemory(tr, db, ticks, textsByStation, chainagesByStation);
        ticks.Sort((a, b) => a.Station.CompareTo(b.Station));

        for (var i = 0; i < ticks.Count; i++)
        {
            var number = increasing
                ? counterStart + i
                : counterStart + ticks.Count - 1 - i;

            var tick = ticks[i];
            DeduplicateLabelList(textsByStation, tick.Station);
            DeduplicateLabelList(chainagesByStation, tick.Station);

            var meta = CrossAxisMetaStore.Load(tr, db, tick.Entity.Handle.Value);
            var nameText = meta?.HasFixedName == true
                ? meta.FixedName!
                : RoadDrawing.FormatStaAttribute(prefix, number);

            ApplyCachedNameLabel(
                tr, db, roadAxisName, tick, nameText, textsByStation, options, createMissingLabels);
            ApplyCachedChainageLabel(
                tr, db, roadAxisName, tick, options, chainagesByStation, createMissingLabels);

            if (!tick.Entity.IsWriteEnabled)
            {
                tick.Entity.UpgradeOpen();
            }

            if (CrossAxisXData.TryReadCrossAxis(tick.Entity, out _, out _) || meta is not null)
            {
                CrossAxisXData.AttachStationTickWithCrossAxis(
                    tick.Entity, roadAxisName, tick.Station, number);
            }
            else
            {
                RoadXData.AttachStationLabel(tick.Entity, roadAxisName, RoadXData.RoleTick, tick.Station);
            }

            if (meta is not null)
            {
                CrossAxisMetaStore.Save(
                    tr, db, tick.Entity.Handle.Value, tick.Station, roadAxisName,
                    number, meta.LeftWidth, meta.RightWidth,
                    string.IsNullOrWhiteSpace(meta.Prefix) ? prefix : meta.Prefix,
                    meta.FixedName);
            }
        }
    }

    private static void CollectAxisStationEntities(
        Transaction tr,
        Database db,
        string roadAxisName,
        out List<TickInfo> ticks,
        out Dictionary<long, List<Entity>> textsByStationKey,
        out Dictionary<long, List<Entity>> chainagesByStationKey)
    {
        ticks = new List<TickInfo>();
        textsByStationKey = new Dictionary<long, List<Entity>>();
        chainagesByStationKey = new Dictionary<long, List<Entity>>();

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            // TIN Face/Mesh nemaju station XData — preskoči (glavni uzrok sporog TCMPOPSTAC).
            if (entity is Face or Solid3d or PolyFaceMesh or SubDMesh)
            {
                continue;
            }

            if (RoadXData.TryReadStationLabel(entity, out var name, out var role, out var station))
            {
                if (!string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (role == RoadXData.RoleTick)
                {
                    ticks.Add(new TickInfo { Entity = entity, Station = station });
                    continue;
                }

                var key = StationKey(station);
                if (role == RoadXData.RoleText)
                {
                    AddToStationMap(textsByStationKey, key, entity);
                }
                else if (role == RoadXData.RoleChainage)
                {
                    AddToStationMap(chainagesByStationKey, key, entity);
                }

                continue;
            }

            // Stari CAXIS-only tick.
            if (!CrossAxisXData.TryReadCrossAxis(entity, out var number, out var parent))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(parent) &&
                !string.Equals(parent, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var meta = CrossAxisMetaStore.Load(tr, db, entity.Handle.Value);
            if (meta is null ||
                !string.Equals(meta.RoadAxisName, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ticks.Any(t => Math.Abs(t.Station - meta.Station) <= StationTolerance))
            {
                continue;
            }

            if (!entity.IsWriteEnabled)
            {
                entity.UpgradeOpen();
            }

            CrossAxisXData.AttachStationTickWithCrossAxis(
                entity, roadAxisName, meta.Station, number > 0 ? number : meta.AxisNumber);
            ticks.Add(new TickInfo { Entity = entity, Station = meta.Station });
        }
    }

    private static long StationKey(double station) =>
        (long)Math.Round(station / StationTolerance);

    private static void AddToStationMap(Dictionary<long, List<Entity>> map, long key, Entity entity)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<Entity>();
            map[key] = list;
        }

        list.Add(entity);
    }

    private static List<TickInfo> DeduplicateTicksInMemory(
        Transaction tr,
        Database db,
        List<TickInfo> ticks,
        Dictionary<long, List<Entity>> textsByStation,
        Dictionary<long, List<Entity>> chainagesByStation)
    {
        var groups = ticks.GroupBy(t => StationKey(t.Station));
        var result = new List<TickInfo>();
        foreach (var group in groups)
        {
            var items = group.ToList();
            var keep = items.FirstOrDefault(t => CrossAxisXData.TryReadCrossAxis(t.Entity, out _))
                       ?? items[0];
            result.Add(keep);
            foreach (var tick in items)
            {
                if (ReferenceEquals(tick.Entity, keep.Entity))
                {
                    continue;
                }

                var handle = tick.Entity.Handle.Value;
                if (!tick.Entity.IsWriteEnabled)
                {
                    tick.Entity.UpgradeOpen();
                }

                tick.Entity.Erase();
                CrossAxisMetaStore.Remove(tr, db, handle);
            }
        }

        return result;
    }

    private static void DeduplicateLabelList(Dictionary<long, List<Entity>> map, double station)
    {
        var key = StationKey(station);
        if (!map.TryGetValue(key, out var list) || list.Count <= 1)
        {
            return;
        }

        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].IsErased)
            {
                continue;
            }

            if (!list[i].IsWriteEnabled)
            {
                list[i].UpgradeOpen();
            }

            list[i].Erase();
        }

        map[key] = [list[0]];
    }

    private static void ApplyCachedNameLabel(
        Transaction tr,
        Database db,
        string roadAxisName,
        TickInfo tick,
        string nameText,
        Dictionary<long, List<Entity>> textsByStation,
        StationLabelOptions options,
        bool createMissingLabels = true)
    {
        var key = StationKey(tick.Station);
        if (textsByStation.TryGetValue(key, out var list) &&
            list.Count > 0 &&
            list[0] is DBText dbText &&
            !dbText.IsErased)
        {
            if (!dbText.IsWriteEnabled)
            {
                dbText.UpgradeOpen();
            }

            dbText.TextString = nameText;
            RoadXData.AttachStationLabel(dbText, roadAxisName, RoadXData.RoleText, tick.Station);
            return;
        }

        if (createMissingLabels)
        {
            EnsureNameAndChainageLabels(tr, db, roadAxisName, tick, nameText, options);
        }
    }

    private static void ApplyCachedChainageLabel(
        Transaction tr,
        Database db,
        string roadAxisName,
        TickInfo tick,
        StationLabelOptions options,
        Dictionary<long, List<Entity>> chainagesByStation,
        bool createMissingLabels = true)
    {
        var relative = Math.Max(0, tick.Station - options.StartStation);
        var chainageText = ChainageFormatter.Format(relative, options.ChainageFormat);
        var key = StationKey(tick.Station);
        if (chainagesByStation.TryGetValue(key, out var list) &&
            list.Count > 0 &&
            list[0] is DBText dbText &&
            !dbText.IsErased)
        {
            if (!dbText.IsWriteEnabled)
            {
                dbText.UpgradeOpen();
            }

            dbText.TextString = chainageText;
            RoadXData.AttachStationLabel(dbText, roadAxisName, RoadXData.RoleChainage, tick.Station);
            return;
        }

        if (!createMissingLabels)
        {
            return;
        }

        // Fali chainage — Ensure dopuni (zadrži postojeći name ako ga već ima).
        var existingName = FindLabelAtStation(tr, db, roadAxisName, tick.Station, RoadXData.RoleText);
        var nameForEnsure = existingName is DBText existingDb
            ? existingDb.TextString
            : RoadDrawing.FormatStaAttribute("STA ", 1);
        EnsureNameAndChainageLabels(tr, db, roadAxisName, tick, nameForEnsure, options);
    }

    private sealed class TickInfo
    {
        public required Entity Entity { get; init; }
        public required double Station { get; init; }
    }

    private static List<TickInfo> CollectTickEntities(Transaction tr, Database db, string roadAxisName)
    {
        CollectAxisStationEntities(tr, db, roadAxisName, out var ticks, out _, out _);
        return ticks;
    }

    private static void CleanupDuplicateTicks(Transaction tr, Database db, string roadAxisName)
    {
        var ticks = CollectTickEntities(tr, db, roadAxisName);
        var groups = ticks.GroupBy(t => Math.Round(t.Station / StationTolerance) * StationTolerance);
        foreach (var group in groups)
        {
            var items = group.ToList();
            if (items.Count <= 1)
            {
                continue;
            }

            // Zadrži ručnu (CAXIS), inače prvu.
            var keep = items.FirstOrDefault(t => CrossAxisXData.TryReadCrossAxis(t.Entity, out _))
                       ?? items[0];
            foreach (var tick in items)
            {
                if (ReferenceEquals(tick.Entity, keep.Entity))
                {
                    continue;
                }

                var handle = tick.Entity.Handle.Value;
                CleanupLabelsAroundTick(tr, db, roadAxisName, tick.Entity, tick.Station, null);
                if (!tick.Entity.IsWriteEnabled)
                {
                    tick.Entity.UpgradeOpen();
                }

                tick.Entity.Erase();
                CrossAxisMetaStore.Remove(tr, db, handle);
            }
        }
    }

    private static void CleanupLabelsAroundTick(
        Transaction tr,
        Database db,
        string roadAxisName,
        Entity tick,
        double station,
        StationLabelOptions? options)
    {
        var origin = GetEntityPosition(tick);
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        var texts = new List<(Entity Entity, string Role, double Dist)>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (ReferenceEquals(entity, tick))
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var name, out var role, out var labelStation))
            {
                continue;
            }

            if (!string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (role != RoadXData.RoleText && role != RoadXData.RoleChainage)
            {
                continue;
            }

            // Samo oznake vezane za ovu stacionažu (ne susedne ose).
            if (Math.Abs(labelStation - station) > StationTolerance)
            {
                continue;
            }

            texts.Add((entity, role, origin.DistanceTo(GetEntityPosition(entity))));
        }

        foreach (var role in new[] { RoadXData.RoleText, RoadXData.RoleChainage })
        {
            var matches = texts.Where(t => t.Role == role).OrderBy(t => t.Dist).ToList();
            if (matches.Count <= 1)
            {
                continue;
            }

            for (var i = 1; i < matches.Count; i++)
            {
                if (!matches[i].Entity.IsWriteEnabled)
                {
                    matches[i].Entity.UpgradeOpen();
                }

                matches[i].Entity.Erase();
            }
        }
    }

    private static void EraseOrphanStationLabels(Transaction tr, Database db, string roadAxisName)
    {
        var tickStations = CollectTickEntities(tr, db, roadAxisName)
            .Select(t => t.Station)
            .ToList();

        // CAXIS-only tickovi (stari format bez RoleTick) + meta stacionaže — nisu siročad.
        foreach (var info in CrossAxisScanner.Scan(tr, db))
        {
            if (!CrossAxisScanner.TryGetEntity(tr, db, info.Handle, out var entity))
            {
                continue;
            }

            if (!CrossAxisXData.TryReadCrossAxis(entity, out _, out var parent))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(parent) &&
                !string.Equals(parent, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var meta = CrossAxisMetaStore.Load(tr, db, info.Handle);
            if (meta is not null &&
                !tickStations.Any(s => Math.Abs(s - meta.Station) <= StationTolerance))
            {
                tickStations.Add(meta.Station);
            }
            else if (RoadXData.TryReadStationLabel(entity, out _, out var role, out var st) &&
                     role == RoadXData.RoleTick &&
                     !tickStations.Any(s => Math.Abs(s - st) <= StationTolerance))
            {
                tickStations.Add(st);
            }
        }

        foreach (var (_, meta) in CrossAxisMetaStore.LoadAllForRoadAxis(tr, db, roadAxisName))
        {
            if (!tickStations.Any(s => Math.Abs(s - meta.Station) <= StationTolerance))
            {
                tickStations.Add(meta.Station);
            }
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var name, out var role, out var labelStation))
            {
                continue;
            }

            if (!string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (role != RoadXData.RoleText && role != RoadXData.RoleChainage)
            {
                continue;
            }

            if (tickStations.Any(s => Math.Abs(s - labelStation) <= StationTolerance))
            {
                continue;
            }

            toErase.Add(id);
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            if (!entity.IsErased)
            {
                entity.Erase();
            }
        }
    }

    private static void EnsureNameAndChainageLabels(
        Transaction tr,
        Database db,
        string roadAxisName,
        TickInfo tick,
        string nameText,
        StationLabelOptions options)
    {
        var relative = Math.Max(0, tick.Station - options.StartStation);
        var chainageText = ChainageFormatter.Format(relative, options.ChainageFormat);

        var nameLabel = FindLabelAtStation(tr, db, roadAxisName, tick.Station, RoadXData.RoleText);
        var chainageLabel = FindLabelAtStation(tr, db, roadAxisName, tick.Station, RoadXData.RoleChainage);

        if (nameLabel is DBText nameDb)
        {
            if (!nameDb.IsWriteEnabled)
            {
                nameDb.UpgradeOpen();
            }

            nameDb.TextString = nameText;
            RoadXData.AttachStationLabel(nameDb, roadAxisName, RoadXData.RoleText, tick.Station);
        }

        if (chainageLabel is DBText chainDb)
        {
            if (!chainDb.IsWriteEnabled)
            {
                chainDb.UpgradeOpen();
            }

            chainDb.TextString = chainageText;
            RoadXData.AttachStationLabel(chainDb, roadAxisName, RoadXData.RoleChainage, tick.Station);
        }

        if (nameLabel is not null && chainageLabel is not null)
        {
            return;
        }

        // Nedostaju oznake — nacrtaj ih uz štapić.
        if (tick.Entity is not Line line)
        {
            return;
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);
        RoadDrawing.EnsureRegApp(tr, db);
        StationFontPreferences.Load();
        var textStyleId = StationTextStyleHelper.Ensure(tr, db, StationFontPreferences.FontFileName);

        var labelSideSign = Math.Abs(options.LabelSideSign) < 1e-9
            ? RoadDrawing.DefaultLabelSideSign
            : options.LabelSideSign;

        // Smer iz putne ose (rast stacionaže) — ne iz Start→End štapića (može biti obrnut).
        Vector3d roadDir;
        Vector3d labelSideNormal;
        Point3d textTickEnd;
        var axis = AxisGeometryReader.ReadAxis(tr, db, roadAxisName, options.StartStation);
        var axisDir = axis?.SampleDirectionAtStation(tick.Station);
        if (axisDir is not null && axisDir.Value.Length > 1e-9)
        {
            roadDir = axisDir.Value.GetNormal();
            var leftNormal = new Vector3d(-roadDir.Y, roadDir.X, 0);
            if (leftNormal.Length < 1e-9)
            {
                leftNormal = Vector3d.YAxis;
            }
            else
            {
                leftNormal = leftNormal.GetNormal();
            }

            labelSideNormal = leftNormal * Math.Sign(labelSideSign);
            var mid = new Point3d(
                (line.StartPoint.X + line.EndPoint.X) * 0.5,
                (line.StartPoint.Y + line.EndPoint.Y) * 0.5,
                (line.StartPoint.Z + line.EndPoint.Z) * 0.5);
            textTickEnd = labelSideNormal.DotProduct(line.EndPoint - mid) >=
                          labelSideNormal.DotProduct(line.StartPoint - mid)
                ? line.EndPoint
                : line.StartPoint;
        }
        else
        {
            var across = line.EndPoint - line.StartPoint;
            if (across.Length < 1e-9)
            {
                return;
            }

            across = across.GetNormal();
            roadDir = new Vector3d(-across.Y, across.X, 0);
            if (roadDir.Length < 1e-9)
            {
                roadDir = Vector3d.XAxis;
            }
            else
            {
                roadDir = roadDir.GetNormal();
            }

            if (labelSideSign < 0)
            {
                labelSideNormal = -across;
                textTickEnd = line.StartPoint;
            }
            else
            {
                labelSideNormal = across;
                textTickEnd = line.EndPoint;
            }
        }

        var textRotation = Math.Atan2(roadDir.Y, roadDir.X) - Math.PI / 2.0 * Math.Sign(labelSideSign);
        var lineSpacing = options.TextHeight * 1.35;
        var basePos = textTickEnd + labelSideNormal * (RoadDrawing.StationTextGapFromTick + options.TextHeight * 0.25);
        var namePosition = basePos - roadDir * (lineSpacing * 0.5);
        var chainagePosition = basePos + roadDir * (lineSpacing * 0.5);
        var colorIndex = options.StationTextColorIndex;
        if (colorIndex < 1)
        {
            colorIndex = 1;
        }

        if (colorIndex > 255)
        {
            colorIndex = 255;
        }

        var textColor = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
            Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
            (short)colorIndex);

        if (nameLabel is null)
        {
            var created = RoadDrawing.CreateStationDbTextPublic(
                nameText, namePosition, options.TextHeight, textColor, textRotation, textStyleId, db);
            modelSpace.AppendEntity(created);
            tr.AddNewlyCreatedDBObject(created, true);
            RoadXData.AttachStationLabel(created, roadAxisName, RoadXData.RoleText, tick.Station);
        }

        if (chainageLabel is null)
        {
            var created = RoadDrawing.CreateStationDbTextPublic(
                chainageText, chainagePosition, options.TextHeight, textColor, textRotation, textStyleId, db);
            modelSpace.AppendEntity(created);
            tr.AddNewlyCreatedDBObject(created, true);
            RoadXData.AttachStationLabel(created, roadAxisName, RoadXData.RoleChainage, tick.Station);
        }
    }

    private static Entity? FindLabelAtStation(
        Transaction tr,
        Database db,
        string roadAxisName,
        double station,
        string role)
    {
        var origin = Point3d.Origin;
        // prefer closest to any tick at this station
        var ticks = CollectTickEntities(tr, db, roadAxisName)
            .Where(t => Math.Abs(t.Station - station) <= StationTolerance)
            .ToList();
        if (ticks.Count > 0)
        {
            origin = GetEntityPosition(ticks[0].Entity);
        }

        Entity? best = null;
        var bestDist = double.MaxValue;
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var name, out var labelRole, out var labelStation) ||
                labelRole != role)
            {
                continue;
            }

            if (!string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(labelStation - station) > StationTolerance)
            {
                continue;
            }

            var dist = origin.DistanceTo(GetEntityPosition(entity));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = entity;
            }
        }

        return best;
    }

    private static bool DrawNumberedAxis(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        string roadAxisName,
        RoadAxis axis,
        RoadAxisMetadata metadata,
        StationLabelOptions options,
        double station,
        int number,
        CrossAxisDrawParameters parameters,
        out string? error)
    {
        error = null;
        try
        {
            RoadDrawing.EnsureRegApp(tr, db);
            var tick = RoadDrawing.DrawManualCrossAxisStation(
                tr,
                modelSpace,
                axis,
                options,
                roadAxisName,
                station,
                parameters.LeftWidth,
                parameters.RightWidth,
                RoadDrawing.FormatStaAttribute(parameters.Prefix, number),
                number);

            CrossAxisStore.Save(tr, db, tick.Handle.Value, new CrossAxisPlacementSettings());
            CrossAxisMetaStore.Save(
                tr, db, tick.Handle.Value, station, roadAxisName,
                number, parameters.LeftWidth, parameters.RightWidth, parameters.Prefix);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool DrawFixedAxis(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        string roadAxisName,
        RoadAxis axis,
        RoadAxisMetadata metadata,
        StationLabelOptions options,
        double station,
        string fixedName,
        double leftWidth,
        double rightWidth,
        out string? error)
    {
        error = null;

        var internalNumber = CrossAxisScanner.GetNextNumber(tr, db);
        while (CrossAxisScanner.Scan(tr, db).Any(a => a.Number == internalNumber))
        {
            internalNumber++;
        }

        try
        {
            RoadDrawing.EnsureRegApp(tr, db);
            var tick = RoadDrawing.DrawManualCrossAxisStation(
                tr,
                modelSpace,
                axis,
                options,
                roadAxisName,
                station,
                leftWidth,
                rightWidth,
                fixedName,
                internalNumber);

            CrossAxisStore.Save(tr, db, tick.Handle.Value, new CrossAxisPlacementSettings());
            CrossAxisMetaStore.Save(
                tr, db, tick.Handle.Value, station, roadAxisName,
                internalNumber, leftWidth, rightWidth, string.Empty, fixedName);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryDrawMultipleAtStations(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        string roadAxisName,
        RoadAxis axis,
        RoadAxisMetadata metadata,
        CrossAxisDrawParameters parameters,
        IReadOnlyList<double> stations,
        out int drawn,
        out string? error)
    {
        drawn = 0;
        error = null;
        var options = metadata.ToLabelOptions();

        foreach (var station in stations.OrderBy(s => s))
        {
            if (HasManualCrossAxisAtStation(tr, db, roadAxisName, station))
            {
                continue;
            }

            var batch = new CrossAxisDrawParameters
            {
                Station = station,
                LeftWidth = parameters.LeftWidth,
                RightWidth = parameters.RightWidth,
                AutoNaming = parameters.AutoNaming,
                Prefix = parameters.Prefix,
                CounterStart = parameters.CounterStart,
                IncreasingNumbers = parameters.IncreasingNumbers,
                FixedName = parameters.FixedName
            };

            if (!parameters.AutoNaming)
            {
                if (!DrawFixedAxis(
                        tr, db, modelSpace, roadAxisName, axis, metadata, options,
                        station, parameters.FixedName.Trim(), parameters.LeftWidth, parameters.RightWidth, out error))
                {
                    return drawn > 0;
                }
            }
            else if (!DrawNumberedAxis(
                         tr, db, modelSpace, roadAxisName, axis, metadata, options,
                         station, Math.Max(1, parameters.CounterStart), batch, out error))
            {
                return drawn > 0;
            }

            drawn++;
        }

        if (drawn > 0)
        {
            SynchronizeNumbering(tr, db, roadAxisName, metadata, options, parameters);
        }

        return drawn > 0;
    }

    /// <summary>
    /// Posle izmene radijusa: ako krajnja stacionaža nema poprečnu osu (štapić), dodaj je.
    /// </summary>
    public static bool EnsureEndStationCrossAxis(
        Transaction tr,
        Database db,
        string roadAxisName,
        RoadAxis axis,
        RoadAxisMetadata metadata)
    {
        if (axis.Elements.Count == 0)
        {
            return false;
        }

        var endStation = axis.Elements[^1].EndStation;
        if (HasTickAtStation(tr, db, roadAxisName, endStation))
        {
            return false;
        }

        StationFontPreferences.Load();
        var options = metadata.ToLabelOptions();
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var left = StationFontPreferences.CrossAxisLeftLength;
        var right = StationFontPreferences.CrossAxisRightLength;
        var provisional = Math.Max(1, metadata.AxisCounterStart);

        try
        {
            var tick = RoadDrawing.DrawManualCrossAxisStation(
                tr,
                modelSpace,
                axis,
                options,
                roadAxisName,
                endStation,
                left,
                right,
                RoadDrawing.FormatStaAttribute(metadata.Prefix, provisional),
                provisional);

            CrossAxisStore.Save(tr, db, tick.Handle.Value, new CrossAxisPlacementSettings());
            CrossAxisMetaStore.Save(
                tr, db, tick.Handle.Value, endStation, roadAxisName,
                provisional, left, right, metadata.Prefix);

            SynchronizeNumbering(tr, db, roadAxisName, metadata, options);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasTickAtStation(
        Transaction tr,
        Database db,
        string roadAxisName,
        double station)
    {
        const double tolerance = 1e-3;
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var name, out var role, out var labelStation) ||
                role != RoadXData.RoleTick)
            {
                continue;
            }

            if (string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(labelStation - station) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Posle osvežavanja osovine ponovo nacrtaj samo ručne poprečne ose (ne intervalne).
    /// Meta se čita PRE purge-a — DeleteLabels briše tick-ove, pa handle više nije živ.
    /// </summary>
    public static void RestoreManualCrossAxisAnnotations(
        Transaction tr,
        Database db,
        string roadAxisName,
        RoadAxis axis,
        RoadAxisMetadata metadata,
        StationLabelOptions options)
    {
        // Snapshot pre PurgeInvalid — inače se izgube stacionaže obrisanih tick-ova.
        var snapshot = CrossAxisMetaStore.LoadAllForRoadAxis(tr, db, roadAxisName)
            .Select(p => p.Meta)
            .OrderBy(m => m.Station)
            .ToList();

        var intervalStations = RoadDrawing.CollectStationsForSync(axis, options);
        var manuals = new List<CrossAxisMeta>();
        foreach (var meta in snapshot)
        {
            if (intervalStations.Any(s => Math.Abs(s - meta.Station) <= StationTolerance))
            {
                continue;
            }

            if (manuals.Any(m => Math.Abs(m.Station - meta.Station) <= StationTolerance))
            {
                continue;
            }

            manuals.Add(meta);
        }

        // Obriši mrtve handle ključeve; stacionaže su već u 'manuals'.
        foreach (var (handle, _) in CrossAxisMetaStore.LoadAllForRoadAxis(tr, db, roadAxisName).ToList())
        {
            CrossAxisMetaStore.Remove(tr, db, handle);
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        StationFontPreferences.Load();
        var axisEnd = axis.Elements.Count > 0 ? axis.Elements[^1].EndStation : axis.StartStation;

        foreach (var meta in manuals)
        {
            if (meta.Station < axis.StartStation - StationTolerance ||
                meta.Station > axisEnd + StationTolerance)
            {
                continue;
            }

            if (HasManualCrossAxisAtStation(tr, db, roadAxisName, meta.Station))
            {
                // Intervalni tick već postoji na istoj staci — sačuvaj meta na njemu ako je CAXIS.
                TryRebindMetaToExistingTick(tr, db, roadAxisName, meta);
                continue;
            }

            DeduplicateStationLabels(tr, db, roadAxisName, meta.Station);

            var labelText = meta.HasFixedName
                ? meta.FixedName!
                : RoadDrawing.FormatStaAttribute(
                    string.IsNullOrWhiteSpace(meta.Prefix) ? metadata.Prefix : meta.Prefix,
                    meta.AxisNumber > 0 ? meta.AxisNumber : 1);

            var left = meta.LeftWidth > 1e-6 ? meta.LeftWidth : StationFontPreferences.CrossAxisLeftLength;
            var right = meta.RightWidth > 1e-6 ? meta.RightWidth : StationFontPreferences.CrossAxisRightLength;
            var number = meta.AxisNumber > 0 ? meta.AxisNumber : 1;

            try
            {
                var tick = RoadDrawing.DrawManualCrossAxisStation(
                    tr,
                    modelSpace,
                    axis,
                    options,
                    roadAxisName,
                    meta.Station,
                    left,
                    right,
                    labelText,
                    number);

                CrossAxisMetaStore.Save(
                    tr, db, tick.Handle.Value, meta.Station, roadAxisName,
                    number, left, right, meta.Prefix, meta.FixedName);
            }
            catch
            {
                // Stacionaža van geometrije posle trim-a — preskoči.
            }
        }

        SynchronizeNumbering(tr, db, roadAxisName, metadata, options);
    }

    private static void TryRebindMetaToExistingTick(
        Transaction tr,
        Database db,
        string roadAxisName,
        CrossAxisMeta meta)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var name, out var role, out var station) ||
                role != RoadXData.RoleTick)
            {
                continue;
            }

            if (!string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(station - meta.Station) > StationTolerance)
            {
                continue;
            }

            if (!entity.IsWriteEnabled)
            {
                entity.UpgradeOpen();
            }

            var number = meta.AxisNumber > 0 ? meta.AxisNumber : 1;
            CrossAxisXData.AttachStationTickWithCrossAxis(entity, roadAxisName, meta.Station, number);
            CrossAxisMetaStore.Save(
                tr, db, entity.Handle.Value, meta.Station, roadAxisName,
                number,
                meta.LeftWidth > 1e-6 ? meta.LeftWidth : StationFontPreferences.CrossAxisLeftLength,
                meta.RightWidth > 1e-6 ? meta.RightWidth : StationFontPreferences.CrossAxisRightLength,
                meta.Prefix,
                meta.FixedName);
            return;
        }
    }

    /// <summary>
    /// Jedinstvena numeracija svih oznaka (interval + ručne) po rastućoj stacionaži.
    /// </summary>
    public static void RenumberAllStationLabels(
        Transaction tr,
        Database db,
        string roadAxisName,
        RoadAxis axis,
        RoadAxisMetadata metadata,
        StationLabelOptions options) =>
        SynchronizeNumbering(tr, db, roadAxisName, metadata, options);

    private static void DeduplicateStationLabels(
        Transaction tr,
        Database db,
        string roadAxisName,
        double station)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        var byRole = new Dictionary<string, List<Entity>>(StringComparer.Ordinal);
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var name, out var role, out var labelStation))
            {
                continue;
            }

            if (!string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(labelStation - station) > StationTolerance)
            {
                continue;
            }

            if (!byRole.TryGetValue(role, out var list))
            {
                list = new List<Entity>();
                byRole[role] = list;
            }

            list.Add(entity);
        }

        foreach (var pair in byRole)
        {
            if (pair.Value.Count <= 1)
            {
                continue;
            }

            // Zadrži prvi sa CAXIS (ručna osa), inače prvi.
            var keep = pair.Value.FirstOrDefault(e => CrossAxisXData.TryReadCrossAxis(e, out _))
                       ?? pair.Value[0];
            foreach (var entity in pair.Value)
            {
                if (ReferenceEquals(entity, keep))
                {
                    continue;
                }

                if (!entity.IsWriteEnabled)
                {
                    entity.UpgradeOpen();
                }

                entity.Erase();
            }
        }
    }

    private static bool HasManualCrossAxisAtStation(
        Transaction tr,
        Database db,
        string roadAxisName,
        double station)
    {
        const double tolerance = 1e-3;
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(entity, out var name, out var role, out var labelStation) ||
                role != RoadXData.RoleTick)
            {
                continue;
            }

            if (!string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(labelStation - station) > tolerance)
            {
                continue;
            }

            // Blokiraj samo ako već postoji ručna (CAXIS) ili bilo koji tick na istoj stacionaži.
            return true;
        }

        return false;
    }

    private static Point3d GetEntityPosition(Entity entity) =>
        entity switch
        {
            DBText dbText => dbText.Position,
            MText mText => mText.Location,
            Line line => new Point3d(
                (line.StartPoint.X + line.EndPoint.X) * 0.5,
                (line.StartPoint.Y + line.EndPoint.Y) * 0.5,
                (line.StartPoint.Z + line.EndPoint.Z) * 0.5),
            _ => Point3d.Origin
        };
}
