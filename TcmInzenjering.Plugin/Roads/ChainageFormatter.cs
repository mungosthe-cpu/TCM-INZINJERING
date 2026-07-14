using System.Globalization;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// CROSS_CHAINAGE_TYPE formati 1–20 (stacionaže na poprečnim osama).
/// </summary>
public static class ChainageFormatter
{
    public const int DefaultFormat = 11;
    public const int MinFormat = 1;
    public const int MaxFormat = 20;

    private static readonly string[] SampleLabels =
    [
        "-1.0-5.00",
        "-1-5.00",
        "-1005.00",
        "-5.00",
        "-1.0-5.000",
        "-1-5.000",
        "-1005.000",
        "-5.000",
        "-10-5.00",
        "-1.0-05.00",
        "-1-005.00",
        "-05.00",
        "-10-05.00",
        "-10-5.000",
        "-1.0-05.000",
        "-1-005.000",
        "-05.000",
        "-10-05.000",
        "-1.0",
        "-1.00500"
    ];

    public static int ClampFormat(int format) =>
        format < MinFormat ? DefaultFormat : format > MaxFormat ? MaxFormat : format;

    public static string GetSampleLabel(int format) =>
        SampleLabels[ClampFormat(format) - 1];

    public static IReadOnlyList<(int Index, string Sample)> GetAllSamples()
    {
        var list = new List<(int, string)>(MaxFormat);
        for (var i = MinFormat; i <= MaxFormat; i++)
        {
            list.Add((i, SampleLabels[i - 1]));
        }

        return list;
    }

    public static string Format(double station, int format)
    {
        format = ClampFormat(format);
        var kilometers = (int)Math.Floor(station / 1000.0);
        var meters = station - kilometers * 1000.0;
        var hundreds = (int)Math.Floor(station / 100.0);
        var remainderHundreds = station - hundreds * 100.0;
        var inv = CultureInfo.InvariantCulture;

        return format switch
        {
            1 => $"{kilometers.ToString("0.0", inv)}-{meters.ToString("0.00", inv)}",
            2 => $"{kilometers}-{meters.ToString("0.00", inv)}",
            3 => station.ToString("0.00", inv),
            4 => meters.ToString("0.00", inv),
            5 => $"{kilometers.ToString("0.0", inv)}-{meters.ToString("0.000", inv)}",
            6 => $"{kilometers}-{meters.ToString("0.000", inv)}",
            7 => station.ToString("0.000", inv),
            8 => meters.ToString("0.000", inv),
            9 => $"{hundreds}-{remainderHundreds.ToString("0.00", inv)}",
            10 => $"{kilometers.ToString("0.0", inv)}-{meters.ToString("00.00", inv)}",
            11 => $"{kilometers}-{meters.ToString("000.00", inv)}",
            12 => meters.ToString("00.00", inv),
            13 => $"{hundreds}-{remainderHundreds.ToString("00.00", inv)}",
            14 => $"{hundreds}-{remainderHundreds.ToString("0.000", inv)}",
            15 => $"{kilometers.ToString("0.0", inv)}-{meters.ToString("00.000", inv)}",
            16 => $"{kilometers}-{meters.ToString("000.000", inv)}",
            17 => meters.ToString("00.000", inv),
            18 => $"{hundreds}-{remainderHundreds.ToString("00.000", inv)}",
            19 => (station / 1000.0).ToString("0.0", inv),
            20 => (station / 1000.0).ToString("0.00000", inv),
            _ => $"{kilometers}-{meters.ToString("000.00", inv)}"
        };
    }
}
