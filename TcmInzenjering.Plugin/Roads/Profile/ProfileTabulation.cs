using Autodesk.AutoCAD.DatabaseServices;
using TcmInzenjering.Plugin.Roads.CrossAxis;

namespace TcmInzenjering.Plugin.Roads.Profile;

public enum ProfileTabulationMode
{
    /// <summary>Korisnički korak (10/20/25/50…).</summary>
    FixedInterval = 0,

    /// <summary>Samo poprečne ose iz situacije (glavni izvor).</summary>
    CrossAxes = 1,

    /// <summary>Poprečne ose iz situacije + podeoci između.</summary>
    CrossAxesAndBetween = 2
}

/// <summary>Stacionaža sa STA brojem iz situacije.</summary>
internal readonly record struct SituationStation(double Station, int Number, bool IsSituationTick);

internal static class ProfileTabulation
{
    private const double Tolerance = 1e-3;

    public static double ResolveStep(
        ProfileTabulationMode mode,
        double fixedInterval,
        double crossAxisInterval,
        int betweenDivisor)
    {
        var cross = crossAxisInterval > 1e-6 ? crossAxisInterval : Math.Max(fixedInterval, 1e-6);
        return mode switch
        {
            ProfileTabulationMode.CrossAxes => cross,
            ProfileTabulationMode.CrossAxesAndBetween =>
                cross / Math.Max(1, betweenDivisor),
            _ => Math.Max(fixedInterval, 1e-6)
        };
    }

    public static IReadOnlyList<double> CollectStations(
        double startStation,
        double endStation,
        ProfileTabulationMode mode,
        double fixedInterval,
        double crossAxisInterval,
        int betweenDivisor)
    {
        var step = ResolveStep(mode, fixedInterval, crossAxisInterval, betweenDivisor);
        var set = new SortedSet<double>();

        set.Add(Round(startStation));
        set.Add(Round(endStation));

        if (mode is ProfileTabulationMode.CrossAxes or ProfileTabulationMode.CrossAxesAndBetween)
        {
            var cross = crossAxisInterval > 1e-6 ? crossAxisInterval : step;
            for (var s = startStation; s <= endStation + 1e-6; s += cross)
            {
                set.Add(Round(s));
            }
        }

        for (var s = startStation; s <= endStation + 1e-6; s += step)
        {
            set.Add(Round(s));
        }

        return set.ToList();
    }

    /// <summary>
    /// Situacija = master: kolone podužnog = žive poprečne ose sa istim STA N i rastojanjima.
    /// </summary>
    public static IReadOnlyList<double> CollectStationsWithLiveCrossAxes(
        Transaction tr,
        Database db,
        string axisName,
        double startStation,
        double endStation,
        ProfileTabulationMode mode,
        double fixedInterval,
        double crossAxisInterval,
        int betweenDivisor)
    {
        return CollectSituationStations(
                tr, db, axisName, startStation, endStation, mode,
                fixedInterval, crossAxisInterval, betweenDivisor)
            .Select(s => s.Station)
            .ToList();
    }

    /// <summary>
    /// Žive poprečne ose iz situacije (+ opciono podeoci), sa STA brojem sa crteža.
    /// </summary>
    public static IReadOnlyList<SituationStation> CollectSituationStations(
        Transaction tr,
        Database db,
        string axisName,
        double startStation,
        double endStation,
        ProfileTabulationMode mode,
        double fixedInterval,
        double crossAxisInterval,
        int betweenDivisor)
    {
        var live = CollectLiveTicks(tr, db, axisName, startStation, endStation);
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        var counterStart = Math.Max(1, metadata?.AxisCounterStart ?? 1);

        // Dopuni brojeve ako fale (redosled kao SynchronizeNumbering).
        for (var i = 0; i < live.Count; i++)
        {
            if (live[i].Number <= 0)
            {
                live[i] = live[i] with { Number = counterStart + i };
            }
        }

        if (live.Count > 0 &&
            mode is ProfileTabulationMode.CrossAxes or ProfileTabulationMode.CrossAxesAndBetween)
        {
            if (mode == ProfileTabulationMode.CrossAxes)
            {
                return live;
            }

            // CrossAxesAndBetween: situacija + podeoci između (bez STA broja).
            return InsertBetweenStations(live, ResolveStep(mode, fixedInterval, crossAxisInterval, betweenDivisor));
        }

        // FixedInterval ili nema živih tick-ova — sintetički grid + live gde postoji.
        var synthetic = CollectStations(
            startStation, endStation, mode, fixedInterval, crossAxisInterval, betweenDivisor);
        var byStation = live.ToDictionary(s => StationKey(s.Station), s => s);
        var result = new List<SituationStation>();
        foreach (var s in synthetic)
        {
            if (byStation.TryGetValue(StationKey(s), out var hit))
            {
                result.Add(hit);
            }
            else
            {
                result.Add(new SituationStation(s, 0, IsSituationTick: false));
            }
        }

        // Live tick van sintetičkog grida — dodaj.
        foreach (var tick in live)
        {
            if (result.All(r => Math.Abs(r.Station - tick.Station) > Tolerance))
            {
                result.Add(tick);
            }
        }

        result.Sort((a, b) => a.Station.CompareTo(b.Station));
        return result;
    }

    private static List<SituationStation> CollectLiveTicks(
        Transaction tr,
        Database db,
        string axisName,
        double startStation,
        double endStation)
    {
        // stationKey → best (prefer CAXIS number)
        var map = new Dictionary<long, SituationStation>();

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (entity is Face or Solid3d or PolyFaceMesh or SubDMesh)
            {
                continue;
            }

            double station;
            int number = 0;
            string? tickAxis = null;

            if (RoadXData.TryReadStationLabel(entity, out var name, out var role, out station) &&
                role == RoadXData.RoleTick &&
                string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                tickAxis = name;
                if (CrossAxisXData.TryReadCrossAxis(entity, out var n, out _))
                {
                    number = n;
                }
            }
            else if (CrossAxisXData.TryReadCrossAxis(entity, out number, out var parent) &&
                     (string.IsNullOrWhiteSpace(parent) ||
                      string.Equals(parent, axisName, StringComparison.OrdinalIgnoreCase)))
            {
                var meta = CrossAxisMetaStore.Load(tr, db, entity.Handle.Value);
                if (meta is null ||
                    !string.Equals(meta.RoadAxisName, axisName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                station = meta.Station;
                tickAxis = axisName;
                if (number <= 0)
                {
                    number = meta.AxisNumber;
                }
            }
            else
            {
                continue;
            }

            if (station < startStation - Tolerance || station > endStation + Tolerance)
            {
                continue;
            }

            _ = tickAxis;
            var key = StationKey(station);
            if (!map.TryGetValue(key, out var existing) ||
                (existing.Number <= 0 && number > 0))
            {
                map[key] = new SituationStation(Round(station), number, IsSituationTick: true);
            }
        }

        // Meta store — ručne ose koje možda nemaju RoleTick u MS (retko).
        foreach (var (_, meta) in CrossAxisMetaStore.LoadAllForRoadAxis(tr, db, axisName))
        {
            if (meta.Station < startStation - Tolerance || meta.Station > endStation + Tolerance)
            {
                continue;
            }

            var key = StationKey(meta.Station);
            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = new SituationStation(Round(meta.Station), meta.AxisNumber, IsSituationTick: true);
            }
            else if (existing.Number <= 0 && meta.AxisNumber > 0)
            {
                map[key] = existing with { Number = meta.AxisNumber };
            }
        }

        return map.Values.OrderBy(s => s.Station).ToList();
    }

    private static List<SituationStation> InsertBetweenStations(
        IReadOnlyList<SituationStation> live,
        double step)
    {
        if (live.Count == 0 || step < 1e-6)
        {
            return live.ToList();
        }

        var result = new List<SituationStation>();
        for (var i = 0; i < live.Count; i++)
        {
            result.Add(live[i]);
            if (i + 1 >= live.Count)
            {
                continue;
            }

            var a = live[i].Station;
            var b = live[i + 1].Station;
            for (var s = a + step; s < b - Tolerance; s += step)
            {
                result.Add(new SituationStation(Round(s), 0, IsSituationTick: false));
            }
        }

        return result;
    }

    private static long StationKey(double station) =>
        (long)Math.Round(station / Tolerance);

    private static double Round(double station) => Math.Round(station, 6);
}
