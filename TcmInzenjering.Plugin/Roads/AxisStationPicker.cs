using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace TcmInzenjering.Plugin.Roads;

internal static class AxisStationPicker
{
    public static bool TryPickStation(
        Document doc,
        RoadAxis axis,
        RoadAxisMetadata? metadata,
        string prompt,
        out double station,
        double? previewLeft = null,
        double? previewRight = null)
    {
        station = 0;
        if (doc is null || axis.Elements.Count == 0)
        {
            return false;
        }

        using var docLock = doc.LockDocument();
        var ed = doc.Editor;
        var left = previewLeft ?? ResolvePreviewHalf(metadata);
        var right = previewRight ?? left;
        var startStation = metadata?.StartStation ?? axis.StartStation;
        var chainageFormat = metadata?.ChainageFormat ?? ChainageFormatter.DefaultFormat;

        var jig = new CrossAxisStationJig(
            axis,
            left,
            right,
            startStation,
            chainageFormat,
            prompt,
            allowNone: false);

        var result = ed.Drag(jig);
        if (result.Status != PromptStatus.OK)
        {
            return false;
        }

        station = jig.Station;

        // Fallback ako jig nije uspeo da projektuje — stara putanja.
        if (station <= axis.StartStation - 1e-6 &&
            TryFindStationAtPoint(axis, jig.Cursor, out var fallback))
        {
            station = fallback;
        }

        return true;
    }

    /// <summary>
    /// Više tačaka u crtežu; Enter završava izbor. Tokom pomeranja miša vidi se poprečna osa + stacionaža.
    /// </summary>
    public static int TryPickMultipleStations(
        Document doc,
        RoadAxis axis,
        out List<double> stations,
        RoadAxisMetadata? metadata = null,
        double? previewLeft = null,
        double? previewRight = null)
    {
        stations = new List<double>();
        if (doc is null || axis.Elements.Count == 0)
        {
            return 0;
        }

        using var docLock = doc.LockDocument();
        var ed = doc.Editor;
        var left = previewLeft ?? ResolvePreviewHalf(metadata);
        var right = previewRight ?? left;
        var startStation = metadata?.StartStation ?? axis.StartStation;
        var chainageFormat = metadata?.ChainageFormat ?? ChainageFormatter.DefaultFormat;

        while (true)
        {
            var jig = new CrossAxisStationJig(
                axis,
                left,
                right,
                startStation,
                chainageFormat,
                "Izaberite položaj poprečne ose (Enter = kraj):",
                allowNone: true);

            var result = ed.Drag(jig);
            if (jig.NoneAccepted)
            {
                break;
            }

            if (result.Status == PromptStatus.Cancel)
            {
                stations.Clear();
                return 0;
            }

            if (result.Status != PromptStatus.OK)
            {
                continue;
            }

            var station = Math.Max(axis.StartStation, Math.Min(jig.Station, axis.Elements[^1].EndStation));
            stations.Add(station);
            ed.WriteMessage($"\nTCM-ROADS: Dodata stacionaža {FormatStationLabel(station, startStation, chainageFormat)}.");
        }

        return stations.Count;
    }

    public static bool TryFindStationAtPoint(RoadAxis axis, Point3d point, out double station)
    {
        station = axis.StartStation;
        var bestDistance = double.MaxValue;
        var found = false;

        foreach (var element in axis.Elements)
        {
            if (!TryClosestOnElement(element, point, out var elementStation, out var distance))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                station = elementStation;
                found = true;
            }
        }

        return found;
    }

    private static double ResolvePreviewHalf(RoadAxisMetadata? metadata)
    {
        if (metadata is { TickLength: > 1e-6 })
        {
            return Math.Max(0.1, metadata.TickLength * 0.5);
        }

        StationFontPreferences.Load();
        return Math.Max(0.1, StationFontPreferences.CrossAxisLeftLength);
    }

    private static string FormatStationLabel(double station, double startStation, int chainageFormat) =>
        ChainageFormatter.Format(Math.Max(0, station - startStation), chainageFormat);

    private sealed class CrossAxisStationJig : DrawJig
    {
        private readonly RoadAxis _axis;
        private readonly double _left;
        private readonly double _right;
        private readonly double _startStation;
        private readonly int _chainageFormat;
        private readonly string _prompt;
        private readonly bool _allowNone;
        private Point3d _cursor;
        private bool _hasGeometry;
        private bool _noneAccepted;

        public double Station { get; private set; }
        public Point3d Cursor => _cursor;
        public bool NoneAccepted => _noneAccepted;

        public CrossAxisStationJig(
            RoadAxis axis,
            double left,
            double right,
            double startStation,
            int chainageFormat,
            string prompt,
            bool allowNone)
        {
            _axis = axis;
            _left = Math.Max(0.1, left);
            _right = Math.Max(0.1, right);
            _startStation = startStation;
            _chainageFormat = chainageFormat;
            _prompt = prompt;
            _allowNone = allowNone;
            Station = axis.StartStation;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var label = _hasGeometry
                ? FormatStationLabel(Station, _startStation, _chainageFormat)
                : "—";
            var options = new JigPromptPointOptions($"\n{_prompt}  [{label}]")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates
            };
            if (_allowNone)
            {
                options.UserInputControls |= UserInputControls.NullResponseAccepted;
            }

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.None)
            {
                _noneAccepted = true;
                return SamplerStatus.Cancel;
            }

            if (result.Status == PromptStatus.Cancel)
            {
                return SamplerStatus.Cancel;
            }

            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.NoChange;
            }

            if (_cursor.DistanceTo(result.Value) < 1e-9)
            {
                return SamplerStatus.NoChange;
            }

            _cursor = result.Value;
            if (TryFindStationAtPoint(_axis, _cursor, out var station))
            {
                Station = Math.Max(
                    _axis.StartStation,
                    Math.Min(station, _axis.Elements[^1].EndStation));
                _hasGeometry = true;
            }
            else
            {
                _hasGeometry = false;
            }

            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            if (!_hasGeometry)
            {
                return true;
            }

            var point = _axis.GetPointAtStation(Station);
            var direction = _axis.SampleDirectionAtStation(Station);
            if (point is null || direction is null || direction.Value.Length < 1e-9)
            {
                return true;
            }

            var roadDir = direction.Value.GetNormal();
            var leftNormal = new Vector3d(-roadDir.Y, roadDir.X, 0);
            if (leftNormal.Length < 1e-9)
            {
                return true;
            }

            leftNormal = leftNormal.GetNormal();
            var start = point.Value - leftNormal * _right;
            var end = point.Value + leftNormal * _left;
            var geometry = draw.Geometry;
            if (geometry is null)
            {
                return true;
            }

            var line = new Line(start, end)
            {
                Color = Color.FromColorIndex(ColorMethod.ByAci, 3)
            };
            var label = FormatStationLabel(Station, _startStation, _chainageFormat);
            var textPos = end + leftNormal * Math.Max(1.0, _left * 0.05);
            var text = new DBText
            {
                Position = textPos,
                Height = Math.Max(1.0, Math.Min(_left, _right) * 0.12),
                TextString = label,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 3),
                Rotation = Math.Atan2(roadDir.Y, roadDir.X)
            };

            try
            {
                geometry.Draw(line);
                geometry.Draw(text);
            }
            finally
            {
                line.Dispose();
                text.Dispose();
            }

            return true;
        }
    }

    private static bool TryClosestOnElement(
        AlignmentElement element,
        Point3d point,
        out double station,
        out double distance)
    {
        station = element.StartStation;
        distance = double.MaxValue;

        if (element.Type == AlignmentElementType.Tangent)
        {
            var seg = new LineSegment3d(element.Start, element.End);
            var closest = seg.GetClosestPointTo(point);
            var along = element.Start.DistanceTo(closest.Point);
            var ratio = element.Length > 1e-9 ? along / element.Length : 0;
            station = element.StartStation + ratio * element.Length;
            distance = point.DistanceTo(closest.Point);
            return true;
        }

        if (element.Type == AlignmentElementType.Arc && element.Radius > 1e-9)
        {
            const int arcSamples = 64;
            var arcFound = false;
            for (var i = 0; i <= arcSamples; i++)
            {
                var t = i / (double)arcSamples;
                var st = element.StartStation + t * element.Length;
                var sample = SamplePointOnElement(element, t);
                if (sample is null)
                {
                    continue;
                }

                var dist = point.DistanceTo(sample.Value);
                if (dist < distance)
                {
                    distance = dist;
                    station = st;
                    arcFound = true;
                }
            }

            return arcFound;
        }

        const int samples = 48;
        var found = false;
        for (var i = 0; i <= samples; i++)
        {
            var t = i / (double)samples;
            var st = element.StartStation + t * element.Length;
            var sample = SamplePointOnElement(element, t);
            if (sample is null)
            {
                continue;
            }

            var dist = point.DistanceTo(sample.Value);
            if (dist < distance)
            {
                distance = dist;
                station = st;
                found = true;
            }
        }

        return found;
    }

    private static Point3d? SamplePointOnElement(AlignmentElement element, double ratio)
    {
        ratio = Math.Max(0, Math.Min(1, ratio));
        if (element.Type == AlignmentElementType.Tangent)
        {
            var dir = element.End - element.Start;
            return element.Start + dir * ratio;
        }

        if (element.Type == AlignmentElementType.Spiral &&
            element.SpiralPoints is { Count: >= 2 } pts)
        {
            var index = ratio * (pts.Count - 1);
            var i0 = (int)Math.Floor(index);
            var i1 = Math.Min(i0 + 1, pts.Count - 1);
            var local = index - i0;
            return pts[i0] + (pts[i1] - pts[i0]) * local;
        }

        if (element.Type == AlignmentElementType.Arc && element.Radius > 1e-9)
        {
            var startVec = element.Start - element.Center;
            var endVec = element.End - element.Center;
            var startAngle = Math.Atan2(startVec.Y, startVec.X);
            var endAngle = Math.Atan2(endVec.Y, endVec.X);
            var angle = InterpolateAngle(startAngle, endAngle, element.Clockwise, ratio);
            return new Point3d(
                element.Center.X + element.Radius * Math.Cos(angle),
                element.Center.Y + element.Radius * Math.Sin(angle),
                element.Start.Z);
        }

        return null;
    }

    private static double InterpolateAngle(double start, double end, bool clockwise, double ratio)
    {
        start = Normalize(start);
        end = Normalize(end);
        if (clockwise)
        {
            if (end > start)
            {
                end -= Math.PI * 2;
            }

            return start + (end - start) * ratio;
        }

        if (end < start)
        {
            end += Math.PI * 2;
        }

        return start + (end - start) * ratio;
    }

    private static double Normalize(double angle)
    {
        while (angle < 0)
        {
            angle += Math.PI * 2;
        }

        while (angle >= Math.PI * 2)
        {
            angle -= Math.PI * 2;
        }

        return angle;
    }
}
