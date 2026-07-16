using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Snimanje / učitavanje skupa tačaka terena (XYZ tekst) — kasnije TIN u bilo kom crtežu.
/// </summary>
internal static class TerrainPointFile
{
    public const string FileFilter = "TCM tacke terena (*.csv;*.txt;*.xyz)|*.csv;*.txt;*.xyz|Svi fajlovi (*.*)|*.*";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Write(string path, IReadOnlyList<Point3d> points)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TCM-INZINJERING terrain points");
        sb.AppendLine("# X,Y,Z");
        foreach (var p in points)
        {
            sb.Append(p.X.ToString("0.000", Inv)).Append(',')
                .Append(p.Y.ToString("0.000", Inv)).Append(',')
                .Append(p.Z.ToString("0.000", Inv)).AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    public static List<Point3d> Read(string path)
    {
        var lines = File.ReadAllLines(path);
        var points = new List<Point3d>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("X", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split([',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!TryParse(parts[0], out var x) ||
                !TryParse(parts[1], out var y) ||
                !TryParse(parts[2], out var z))
            {
                continue;
            }

            points.Add(new Point3d(x, y, z));
        }

        return points;
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out value);
}
