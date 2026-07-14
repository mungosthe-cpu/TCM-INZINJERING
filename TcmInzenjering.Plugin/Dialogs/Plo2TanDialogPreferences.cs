using System.IO;
using System.Text.Json;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

internal static class Plo2TanDialogPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string PreferencesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "plo2tan-preferences.json");

    public static void ApplyTo(Plo2TanDialogState state)
    {
        if (!File.Exists(PreferencesPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(PreferencesPath);
            var saved = JsonSerializer.Deserialize<Plo2TanDialogState>(json, JsonOptions);
            if (saved is null)
            {
                return;
            }

            state.AxisName = saved.AxisName;
            state.CurveRadius = saved.CurveRadius;
            state.Interval = saved.Interval;
            state.TextHeight = saved.TextHeight;
            state.TickLength = saved.TickLength;
            state.Prefix = saved.Prefix;
            state.AxisCounterStart = saved.AxisCounterStart;
            state.LabelFormat = saved.LabelFormat;
            state.ChainageFormat = saved.ChainageFormat > 0
                ? ChainageFormatter.ClampFormat(saved.ChainageFormat)
                : ChainageFormatter.DefaultFormat;
            state.DrawSegmentLabels = saved.DrawSegmentLabels;
            state.EqualIntervalInBounds = saved.EqualIntervalInBounds;
            state.WholeInterval = saved.WholeInterval;
            state.AlignToStart = saved.AlignToStart;
            state.LabelAtStart = saved.LabelAtStart;
            state.LabelAtEnd = saved.LabelAtEnd;
            state.LabelAtMainPoints = saved.LabelAtMainPoints;
            state.AxisColorIndex = saved.AxisColorIndex;
            state.StationTextColorIndex = saved.StationTextColorIndex;
            state.StationTickColorIndex = saved.StationTickColorIndex;
            state.SegmentLabelColorIndex = saved.SegmentLabelColorIndex;
        }
        catch
        {
            // Koristi podrazumevane vrednosti ako fajl nije validan.
        }
    }

    public static void SaveFrom(Plo2TanDialogState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(PreferencesPath, json);
        }
        catch
        {
            // Ne blokiraj crtanje ako upis podesavanja ne uspe.
        }
    }
}
