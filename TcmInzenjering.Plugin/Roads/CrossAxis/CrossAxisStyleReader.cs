using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal sealed class CrossAxisTemplateStyle
{
    public string RoadAxisName { get; init; } = string.Empty;
    public double LeftWidth { get; init; }
    public double RightWidth { get; init; }
    public double TextHeight { get; init; }
    public string FontFileName { get; init; } = string.Empty;
    public string Prefix { get; init; } = string.Empty;
}

/// <summary>
/// Čita dužine, font i visinu teksta sa postojećih štapića / poprečnih osa na putnoj osovini.
/// </summary>
internal static class CrossAxisStyleReader
{
    private const double StationTolerance = 1e-3;

    public static CrossAxisTemplateStyle ReadFromRoadAxis(
        Transaction tr,
        Database db,
        string roadAxisName,
        RoadAxisMetadata? metadata = null)
    {
        metadata ??= RoadAxisStore.Load(tr, db, roadAxisName);
        StationFontPreferences.Load();

        var leftSamples = new List<double>();
        var rightSamples = new List<double>();
        CollectMeasuredWidths(tr, db, roadAxisName, metadata, leftSamples, rightSamples);

        foreach (var (_, meta) in CrossAxisMetaStore.LoadAllForRoadAxis(tr, db, roadAxisName))
        {
            if (meta.LeftWidth > 1e-6)
            {
                leftSamples.Add(meta.LeftWidth);
            }

            if (meta.RightWidth > 1e-6)
            {
                rightSamples.Add(meta.RightWidth);
            }
        }

        // Preferiraj duže (intervalne) štapiće — medijan od gornje polovine uzoraka.
        var left = PreferLongWidth(leftSamples, fallback: ResolveFallbackHalf(metadata));
        var right = PreferLongWidth(rightSamples, fallback: ResolveFallbackHalf(metadata));

        TryReadLabelStyle(tr, db, roadAxisName, out var textHeight, out var fontFile);
        if (textHeight <= 1e-6)
        {
            textHeight = metadata?.TextHeight > 1e-6 ? metadata.TextHeight : 2.5;
        }

        if (string.IsNullOrWhiteSpace(fontFile))
        {
            fontFile = StationFontPreferences.FontFileName;
        }

        return new CrossAxisTemplateStyle
        {
            RoadAxisName = roadAxisName,
            LeftWidth = left,
            RightWidth = right,
            TextHeight = textHeight,
            FontFileName = fontFile,
            Prefix = metadata?.Prefix ?? "STA "
        };
    }

    private static double ResolveFallbackHalf(RoadAxisMetadata? metadata)
    {
        if (metadata is { TickLength: > 1e-6 })
        {
            return Math.Max(0.1, metadata.TickLength * 0.5);
        }

        StationFontPreferences.Load();
        // Ako su preference još podrazumevane (kratke), uzmi TickLength default.
        if (Math.Abs(StationFontPreferences.CrossAxisLeftLength - StationFontPreferences.DefaultHalfTickLength) < 1e-6)
        {
            return Math.Max(0.1, RoadDrawing.DefaultTickLength * 0.5);
        }

        return StationFontPreferences.CrossAxisLeftLength;
    }

    private static double PreferLongWidth(IReadOnlyList<double> samples, double fallback)
    {
        if (samples.Count == 0)
        {
            return fallback;
        }

        var ordered = samples.Where(v => v > 1e-6).OrderBy(v => v).ToList();
        if (ordered.Count == 0)
        {
            return fallback;
        }

        // Medijan gornje polovine — ignoriše kratke „ručne“ izuzetke.
        var start = ordered.Count / 2;
        var upper = ordered.Skip(start).ToList();
        return upper[upper.Count / 2];
    }

    private static void CollectMeasuredWidths(
        Transaction tr,
        Database db,
        string roadAxisName,
        RoadAxisMetadata? metadata,
        List<double> leftSamples,
        List<double> rightSamples)
    {
        var axis = AxisGeometryReader.ReadAxis(tr, db, roadAxisName, metadata?.StartStation ?? 0);
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Line line)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(line, out var name, out var role, out var station) ||
                !string.Equals(name, roadAxisName, StringComparison.OrdinalIgnoreCase) ||
                role != RoadXData.RoleTick)
            {
                continue;
            }

            if (axis is null)
            {
                var half = line.Length * 0.5;
                if (half > 1e-6)
                {
                    leftSamples.Add(half);
                    rightSamples.Add(half);
                }

                continue;
            }

            var point = axis.GetPointAtStation(station);
            var direction = axis.SampleDirectionAtStation(station);
            if (point is null || direction is null || direction.Value.Length < 1e-9)
            {
                var half = line.Length * 0.5;
                if (half > 1e-6)
                {
                    leftSamples.Add(half);
                    rightSamples.Add(half);
                }

                continue;
            }

            var roadDir = direction.Value.GetNormal();
            var leftNormal = new Vector3d(-roadDir.Y, roadDir.X, 0);
            if (leftNormal.Length < 1e-9)
            {
                continue;
            }

            leftNormal = leftNormal.GetNormal();
            var d1 = leftNormal.DotProduct(line.StartPoint - point.Value);
            var d2 = leftNormal.DotProduct(line.EndPoint - point.Value);
            var left = Math.Max(d1, d2);
            var right = -Math.Min(d1, d2);
            if (left > 1e-6)
            {
                leftSamples.Add(left);
            }

            if (right > 1e-6)
            {
                rightSamples.Add(right);
            }
        }
    }

    private static void TryReadLabelStyle(
        Transaction tr,
        Database db,
        string roadAxisName,
        out double textHeight,
        out string fontFileName)
    {
        textHeight = 0;
        fontFileName = string.Empty;

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not DBText text)
            {
                continue;
            }

            if (!RoadXData.TryReadStationLabel(text, out var axisName, out var role, out _) ||
                !string.Equals(axisName, roadAxisName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (role != RoadXData.RoleText && role != RoadXData.RoleChainage)
            {
                continue;
            }

            if (text.Height > textHeight)
            {
                textHeight = text.Height;
            }

            if (string.IsNullOrWhiteSpace(fontFileName))
            {
                try
                {
                    if (!text.TextStyleId.IsNull &&
                        tr.GetObject(text.TextStyleId, OpenMode.ForRead) is TextStyleTableRecord style &&
                        !string.IsNullOrWhiteSpace(style.FileName))
                    {
                        fontFileName = style.FileName.Trim();
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (textHeight > 1e-6 && !string.IsNullOrWhiteSpace(fontFileName))
            {
                return;
            }
        }
    }
}
