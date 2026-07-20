using System.Globalization;
using System.Windows.Data;

namespace TcmInzenjering.Plugin.Dialogs;

/// <summary>
/// Parses doubles with either '.' or ',' and tolerates incomplete input while typing
/// (e.g. "1." / "1,") so DataGrid cells do not reject decimal entry or throw into AutoCAD.
/// </summary>
public sealed class FlexibleDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double number || double.IsNaN(number) || double.IsInfinity(number))
        {
            return string.Empty;
        }

        return number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Binding.DoNothing;
        }

        // Allow intermediate typing: "1.", "1,", "-", "-1."
        if (text is "." or "," or "-" or "-." or "-,")
        {
            return Binding.DoNothing;
        }

        if (text.EndsWith(".", StringComparison.Ordinal) || text.EndsWith(",", StringComparison.Ordinal))
        {
            var stem = text.Substring(0, text.Length - 1);
            if (stem.Length == 0 || stem == "-")
            {
                return Binding.DoNothing;
            }

            // Keep caret after decimal: do not commit yet.
            return Binding.DoNothing;
        }

        var normalized = text.Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return Binding.DoNothing;
    }
}
