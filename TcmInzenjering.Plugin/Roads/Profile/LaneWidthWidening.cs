namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Empirijsko proširenje kolovoza u krivinama (AASHTO/Plateia stil aproksimacija).
/// Δ ≈ L² / (2R) skalirano brzinom, sa linearnim prelazom.
/// </summary>
internal static class LaneWidthWidening
{
    private const double DesignVehicleLength = 12.0;

    public static double ComputeAutoDelta(
        RoadAxis? axis,
        double station,
        double designSpeedKmh,
        double transitionLength)
    {
        if (axis is null || axis.Elements.Count == 0)
        {
            return 0;
        }

        AlignmentElement? curve = null;
        foreach (var element in axis.Elements)
        {
            if (element.Type is not (AlignmentElementType.Arc or AlignmentElementType.Spiral))
            {
                continue;
            }

            if (station < element.StartStation - 1e-6 || station > element.EndStation + 1e-6)
            {
                continue;
            }

            curve = element;
            break;
        }

        if (curve is null)
        {
            // Prelaz pre/posle krivine.
            return TransitionOutsideCurve(axis, station, designSpeedKmh, transitionLength);
        }

        var radius = EffectiveRadius(curve, station);
        return Formula(radius, designSpeedKmh);
    }

    private static double TransitionOutsideCurve(
        RoadAxis axis,
        double station,
        double designSpeedKmh,
        double transitionLength)
    {
        if (transitionLength < 1e-6)
        {
            return 0;
        }

        double best = 0;
        foreach (var element in axis.Elements)
        {
            if (element.Type is not (AlignmentElementType.Arc or AlignmentElementType.Spiral) ||
                element.Radius < 1e-3)
            {
                continue;
            }

            var peak = Formula(element.Radius, designSpeedKmh);
            if (peak < 1e-6)
            {
                continue;
            }

            if (station < element.StartStation)
            {
                var dist = element.StartStation - station;
                if (dist <= transitionLength)
                {
                    best = Math.Max(best, peak * (1.0 - dist / transitionLength));
                }
            }
            else if (station > element.EndStation)
            {
                var dist = station - element.EndStation;
                if (dist <= transitionLength)
                {
                    best = Math.Max(best, peak * (1.0 - dist / transitionLength));
                }
            }
        }

        return best;
    }

    private static double EffectiveRadius(AlignmentElement element, double station)
    {
        if (element.Type == AlignmentElementType.Arc)
        {
            return Math.Abs(element.Radius);
        }

        // Spiral: linearna interpolacija 0 → R ili R → 0 po dužini.
        var t = (station - element.StartStation) /
                Math.Max(1e-6, element.EndStation - element.StartStation);
        t = Math.Max(0, Math.Min(1, t));
        var r = Math.Abs(element.Radius);
        if (r < 1e-3)
        {
            return 0;
        }

        // Aproksimacija: manji R bliže kraju spirale ka luku.
        return Math.Max(r, r / Math.Max(0.15, t));
    }

    private static double Formula(double radius, double designSpeedKmh)
    {
        if (radius < 1e-3 || radius > 5000)
        {
            return 0;
        }

        var speedFactor = Math.Max(0.6, Math.Min(1.4, designSpeedKmh / 60.0));
        var delta = DesignVehicleLength * DesignVehicleLength / (2.0 * radius) * speedFactor;
        return Math.Max(0, Math.Min(1.5, delta));
    }
}
