#if NET48
using Autodesk.AutoCAD.EditorInput;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

internal static class LegacyPlo2TanPrompt
{
    public static bool TryCollect(Editor ed, double axisLength, out string axisName, out double curveRadius, out double textHeight, out StationLabelOptions stationOptions)
    {
        axisName = "OS-1";
        curveRadius = 50;
        textHeight = 2.5;
        stationOptions = new StationLabelOptions
        {
            EqualIntervalInBounds = true,
            WholeInterval = true,
            StartStation = 0,
            EndStation = axisLength,
            AlignToStart = true,
            LabelAtEnd = true,
            Interval = 20,
            Prefix = "STA ",
            TextHeight = 2.5,
            TickLength = RoadDrawing.DefaultTickLength,
            AxisCounterStart = 1
        };

        var nameResult = ed.GetString("\nIme osovine [OS-1]: ");
        if (nameResult.Status == PromptStatus.Cancel)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(nameResult.StringResult))
        {
            axisName = nameResult.StringResult.Trim();
        }

        var radiusResult = ed.GetDouble("\nRadijus luka [50]: ");
        if (radiusResult.Status == PromptStatus.Cancel)
        {
            return false;
        }

        if (radiusResult.Status == PromptStatus.OK)
        {
            curveRadius = radiusResult.Value;
        }

        var intervalResult = ed.GetDouble("\nInterval stacionaze [20]: ");
        if (intervalResult.Status == PromptStatus.Cancel)
        {
            return false;
        }

        if (intervalResult.Status == PromptStatus.OK)
        {
            stationOptions = new StationLabelOptions
            {
                EqualIntervalInBounds = true,
                WholeInterval = true,
                StartStation = 0,
                EndStation = axisLength,
                AlignToStart = true,
                LabelAtEnd = true,
                Interval = intervalResult.Value,
                Prefix = "STA ",
                TextHeight = 2.5,
                TickLength = RoadDrawing.DefaultTickLength,
                AxisCounterStart = 1
            };
        }

        return true;
    }
}
#endif
