using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

internal enum LaneSide
{
    Left = -1,
    Right = 1
}

internal readonly record struct LaneSlice(
    string LaneId,
    string Label,
    LaneSide Side,
    LaneRole Role,
    double Width,
    double OffsetFromAxisInner,
    double OffsetFromAxisOuter);

internal readonly record struct LaneWidthStationResult(
    string TemplateName,
    IReadOnlyList<LaneSlice> Lanes,
    double LeftCarriageway,
    double RightCarriageway,
    double LeftTotal,
    double RightTotal,
    double WideningLeft,
    double WideningRight);

internal static class LaneWidthEvaluator
{
    public static LaneWidthType? ResolveType(
        LaneWidthDefinitionSet model,
        double station)
    {
        LaneTypeAssignment? match = null;
        foreach (var assignment in model.Assignments
                     .OrderBy(item => item.StartStation))
        {
            if (station >= assignment.StartStation - 1e-6 &&
                station <= assignment.EndStation + 1e-6)
            {
                match = assignment;
            }
        }

        var typeName = match?.TypeName ?? model.ActiveTypeName;
        return model.Types.FirstOrDefault(type =>
            string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase));
    }

    public static LaneWidthStationResult Evaluate(
        LaneWidthDefinitionSet model,
        RoadAxis? axis,
        double station)
    {
        var type = ResolveType(model, station) ??
                   model.Types.FirstOrDefault() ??
                   new LaneWidthType { Name = "Trenutni" };

        var widenLeft = 0.0;
        var widenRight = 0.0;
        if (model.Widening.Enabled)
        {
            var auto = LaneWidthWidening.ComputeAutoDelta(
                axis, station, model.Widening.DesignSpeedKmh, model.Widening.TransitionLength);
            widenLeft = Math.Max(0, auto + model.Widening.ManualDeltaLeft);
            widenRight = Math.Max(0, auto + model.Widening.ManualDeltaRight);
        }
        else
        {
            widenLeft = Math.Max(0, model.Widening.ManualDeltaLeft);
            widenRight = Math.Max(0, model.Widening.ManualDeltaRight);
        }

        var slices = new List<LaneSlice>();
        var leftCarriageway = Accumulate(
            slices, type.Left, LaneSide.Left, station, widenLeft);
        var rightCarriageway = Accumulate(
            slices, type.Right, LaneSide.Right, station, widenRight);

        return new LaneWidthStationResult(
            type.Name,
            slices,
            leftCarriageway,
            rightCarriageway,
            slices.Where(slice => slice.Side == LaneSide.Left).Sum(slice => slice.Width),
            slices.Where(slice => slice.Side == LaneSide.Right).Sum(slice => slice.Width),
            widenLeft,
            widenRight);
    }

    public static IReadOnlyList<(double Station, LaneWidthStationResult Result)> Sample(
        LaneWidthDefinitionSet model,
        RoadAxis axis,
        double step = 2.0)
    {
        if (axis.Elements.Count == 0)
        {
            return Array.Empty<(double, LaneWidthStationResult)>();
        }

        var start = axis.StartStation;
        var end = axis.Elements[^1].EndStation;
        var stations = new SortedSet<double> { start, end };
        for (var station = start; station < end; station += Math.Max(0.5, step))
        {
            stations.Add(station);
        }

        foreach (var element in axis.Elements)
        {
            stations.Add(element.StartStation);
            stations.Add(element.EndStation);
        }

        foreach (var assignment in model.Assignments)
        {
            stations.Add(assignment.StartStation);
            stations.Add(assignment.EndStation);
        }

        foreach (var type in model.Types)
        {
            foreach (var lane in type.Left.Concat(type.Right))
            {
                foreach (var point in lane.WidthPoints)
                {
                    stations.Add(point.Station);
                }
            }
        }

        return stations
            .Where(station => station >= start - 1e-6 && station <= end + 1e-6)
            .Select(station => (station, Evaluate(model, axis, station)))
            .ToList();
    }

    public static bool TryGetLdAtStation(
        Transaction tr,
        Database db,
        string axisName,
        double station,
        out double leftWidth,
        out double rightWidth)
    {
        leftWidth = 0;
        rightWidth = 0;
        if (!LaneWidthDefinitionStore.HasSavedDefinitions(tr, db, axisName))
        {
            return ProfileLaneWidthStore.TryGetDefaults(
                tr, db, axisName, out leftWidth, out rightWidth);
        }

        ProfileLaneWidthStore.TryGetDefaults(
            tr, db, axisName, out var fallbackLeft, out var fallbackRight);
        var model = LaneWidthDefinitionStore.Load(
            tr, db, axisName, fallbackLeft, fallbackRight);
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        RoadAxis? axis = null;
        if (metadata is not null)
        {
            axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        }

        var result = Evaluate(model, axis, station);
        leftWidth = result.LeftCarriageway;
        rightWidth = result.RightCarriageway;
        return leftWidth > 1e-9 || rightWidth > 1e-9;
    }

    private static double Accumulate(
        List<LaneSlice> slices,
        IReadOnlyList<LaneWidthLane> lanes,
        LaneSide side,
        double station,
        double wideningOnOuterCarriageway)
    {
        var inner = 0.0;
        var carriageway = 0.0;
        var lastCarriagewayIndex = -1;
        for (var i = 0; i < lanes.Count; i++)
        {
            if (lanes[i].IsCarriageway)
            {
                lastCarriagewayIndex = i;
            }
        }

        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var width = lane.WidthAt(station);
            if (i == lastCarriagewayIndex && wideningOnOuterCarriageway > 1e-9)
            {
                width += wideningOnOuterCarriageway;
            }

            var outer = inner + width;
            slices.Add(new LaneSlice(
                lane.Id,
                lane.Label,
                side,
                lane.Role,
                width,
                inner,
                outer));
            if (lane.IsCarriageway)
            {
                carriageway += width;
            }

            inner = outer;
        }

        return carriageway;
    }
}
