namespace TcmInzenjering.Plugin.Roads.Profile;

public enum ProfileTabulationMode
{
    /// <summary>Korisnički korak (10/20/25/50…).</summary>
    FixedInterval = 0,

    /// <summary>Samo stacionaže poprečnih osa (interval ose).</summary>
    CrossAxes = 1,

    /// <summary>Poprečne ose + podeoci između (korak = interval / delilac).</summary>
    CrossAxesAndBetween = 2
}

internal static class ProfileTabulation
{
    /// <summary>
    /// Korak tabeliranja: za pop. ose uvek = crossInterval;
    /// za +između = crossInterval / betweenDivisor (delilac ≥ 1).
    /// </summary>
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

        // Uvek uključi krajeve.
        set.Add(Round(startStation));
        set.Add(Round(endStation));

        if (mode is ProfileTabulationMode.CrossAxes or ProfileTabulationMode.CrossAxesAndBetween)
        {
            // Obavezne stacionaže poprečnih osa.
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

    private static double Round(double station) => Math.Round(station, 6);
}
