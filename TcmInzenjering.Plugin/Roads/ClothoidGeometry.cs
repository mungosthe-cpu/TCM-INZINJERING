using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Klotoida (Euler spiral) za prelaznice: A² = R·L, θ = L/(2R).
/// </summary>
internal static class ClothoidGeometry
{
    private const double Tol = 1e-9;
    private const int SampleCount = 32;

    public sealed class CornerSasResult
    {
        public required Point2d Ts { get; init; }
        public required Point2d Sc { get; init; }
        public required Point2d Cs { get; init; }
        public required Point2d St { get; init; }
        public required Point2d Center { get; init; }
        public required double Radius { get; init; }
        public required double CircularSweepRadians { get; init; }
        public required bool Clockwise { get; init; }
        public required double TangentLength1 { get; init; }
        public required double TangentLength2 { get; init; }
        public required double SpiralAngle1 { get; init; }
        public required double SpiralAngle2 { get; init; }
        public required double L1 { get; init; }
        public required double L2 { get; init; }
        public required IReadOnlyList<Point2d> EntranceSamples { get; init; }
        public required IReadOnlyList<Point2d> ExitSamples { get; init; }
        public double ExternalDistance { get; init; }
    }

    public static bool TryEvaluateClothoidEnd(double length, double endRadius, out double xs, out double ys, out double theta)
    {
        xs = 0;
        ys = 0;
        theta = 0;
        if (length <= Tol || endRadius <= Tol)
        {
            return false;
        }

        var a2 = endRadius * length;
        theta = length * length / (2.0 * a2); // = L/(2R)
        EvaluateFresnel(length, a2, out xs, out ys);
        return true;
    }

    /// <summary>
    /// Grade spiral–arc–spiral u PI. L1 ili L2 = 0 → ta strana bez prelaznice (LR / RL).
    /// </summary>
    public static bool TryBuildCornerSas(
        Point2d pi,
        Vector2d inDir,
        Vector2d outDir,
        double radius,
        double l1,
        double l2,
        double maxIncoming,
        double maxOutgoing,
        out CornerSasResult? result,
        out string? error)
    {
        result = null;
        error = null;

        if (inDir.Length < Tol || outDir.Length < Tol)
        {
            error = "Nevalidan smer tangenti.";
            return false;
        }

        if (radius <= Tol)
        {
            error = "Radijus R mora biti veci od 0.";
            return false;
        }

        l1 = Math.Max(0, l1);
        l2 = Math.Max(0, l2);
        inDir = inDir.GetNormal();
        outDir = outDir.GetNormal();

        var dot = MathNet48.Clamp(inDir.DotProduct(outDir), -1.0, 1.0);
        var deflection = Math.Acos(dot);
        if (deflection < 0.001 || Math.Abs(deflection - Math.PI) < 0.001)
        {
            error = "Ugao tangenti je premalen ili 180° — nije moguce zaobljenje.";
            return false;
        }

        var cross = inDir.X * outDir.Y - inDir.Y * outDir.X;
        var clockwise = cross < 0;
        var sign = clockwise ? -1.0 : 1.0;

        var theta1 = l1 > Tol ? l1 / (2.0 * radius) : 0.0;
        var theta2 = l2 > Tol ? l2 / (2.0 * radius) : 0.0;
        if (theta1 + theta2 >= deflection - 1e-6)
        {
            error =
                $"Sa L1={l1:0.###}, L2={l2:0.###}, R={radius:0.###} prelaznice \"pojedu\" ceo ugao α " +
                $"({deflection * 180 / Math.PI:0.##}°).\n" +
                "Smanjite L1/L2 ili povecajte R (potrebno θ1+θ2 < α).";
            return false;
        }

        var deltaC = deflection - theta1 - theta2;

        double xs1 = 0, ys1 = 0, xs2 = 0, ys2 = 0;
        if (l1 > Tol && !TryEvaluateClothoidEnd(l1, radius, out xs1, out ys1, out _))
        {
            error = "Neuspesna klotoida L1.";
            return false;
        }

        if (l2 > Tol && !TryEvaluateClothoidEnd(l2, radius, out xs2, out ys2, out _))
        {
            error = "Neuspesna klotoida L2.";
            return false;
        }

        var p1 = l1 > Tol ? ys1 - radius * (1.0 - Math.Cos(theta1)) : 0.0;
        var q1 = l1 > Tol ? xs1 - radius * Math.Sin(theta1) : 0.0;
        var p2 = l2 > Tol ? ys2 - radius * (1.0 - Math.Cos(theta2)) : 0.0;
        var q2 = l2 > Tol ? xs2 - radius * Math.Sin(theta2) : 0.0;

        var tanHalf = Math.Tan(deflection / 2.0);
        double t1;
        double t2;
        if (l1 <= Tol)
        {
            // RL: ulaz kružni, izlaz prelaznica
            t1 = (radius + p2) * tanHalf;
            t2 = (radius + p2) * tanHalf + q2;
        }
        else if (l2 <= Tol)
        {
            // LR: ulaz prelaznica, izlaz kružni
            t1 = (radius + p1) * tanHalf + q1;
            t2 = (radius + p1) * tanHalf;
        }
        else
        {
            var pCorr = (p1 - p2) / Math.Sin(deflection) * (1.0 - Math.Cos(deflection)) / 2.0;
            t1 = q1 + (radius + p1) * tanHalf + pCorr;
            t2 = q2 + (radius + p2) * tanHalf - pCorr;
        }

        if (t1 <= Tol || t2 <= Tol)
        {
            error = "Izracunate duzine tangenti T1/T2 nisu validne.";
            return false;
        }

        if (t1 > maxIncoming + 1e-6 || t2 > maxOutgoing + 1e-6)
        {
            error =
                $"Zaobljenje sa R={radius:0.###}, L1={l1:0.###}, L2={l2:0.###} zahteva " +
                $"T1={t1:0.###} / T2={t2:0.###}, a dostupno je " +
                $"{maxIncoming:0.###} / {maxOutgoing:0.###}.\n" +
                "Smanjite R ili L1/L2, ili pomerite susedne cvorove.";
            return false;
        }

        var ts = new Point2d(pi.X - inDir.X * t1, pi.Y - inDir.Y * t1);
        var st = new Point2d(pi.X + outDir.X * t2, pi.Y + outDir.Y * t2);

        IReadOnlyList<Point2d> entrance;
        Point2d sc;
        Vector2d scTangent;
        if (l1 > Tol)
        {
            entrance = SampleClothoidWorld(ts, inDir, l1, radius, sign, SampleCount);
            sc = entrance[^1];
            scTangent = Rotate(inDir, sign * theta1);
        }
        else
        {
            entrance = Array.Empty<Point2d>();
            sc = ts;
            scTangent = inDir;
        }

        // Centar iz SC — G1 na početku kružnog luka.
        var inward = new Vector2d(-scTangent.Y * sign, scTangent.X * sign).GetNormal();
        var center = new Point2d(sc.X + inward.X * radius, sc.Y + inward.Y * radius);

        // CS = SC rotiran oko centra za Δc (isti luk, bez lomljenja).
        var cs = RotateAround(sc, center, sign * deltaC);

        IReadOnlyList<Point2d> exit;
        if (l2 > Tol)
        {
            // Od ST unazad (∞→R) sa -sign: posle reverse tangent na CS = luk (G1).
            var stOnTangent = st;
            var exitBack = SampleClothoidWorld(stOnTangent, outDir.Negate(), l2, radius, -sign, SampleCount);
            var reversed = exitBack.AsEnumerable().Reverse().ToList();
            // Translacija na tačan CS (čuva orijentaciju / G1); ST ostaje na izlaznoj tangenti.
            if (reversed.Count > 0)
            {
                var delta = cs - reversed[0];
                for (var i = 0; i < reversed.Count; i++)
                {
                    reversed[i] = new Point2d(reversed[i].X + delta.X, reversed[i].Y + delta.Y);
                }

                reversed[0] = cs;
                reversed[^1] = stOnTangent;
            }

            exit = reversed;
            st = stOnTangent;
        }
        else
        {
            st = cs;
            exit = Array.Empty<Point2d>();
        }

        var external = (radius + Math.Max(p1, p2)) * (1.0 / Math.Cos(deflection / 2.0) - 1.0);

        result = new CornerSasResult
        {
            Ts = ts,
            Sc = sc,
            Cs = cs,
            St = st,
            Center = center,
            Radius = radius,
            CircularSweepRadians = deltaC,
            Clockwise = clockwise,
            TangentLength1 = t1,
            TangentLength2 = t2,
            SpiralAngle1 = theta1,
            SpiralAngle2 = theta2,
            L1 = l1,
            L2 = l2,
            EntranceSamples = entrance,
            ExitSamples = exit,
            ExternalDistance = external
        };
        return true;
    }

    public static List<Point2d> SampleClothoidWorld(
        Point2d start,
        Vector2d initialDir,
        double length,
        double endRadius,
        double sideSign,
        int samples)
    {
        var pts = new List<Point2d>();
        if (length <= Tol)
        {
            pts.Add(start);
            return pts;
        }

        initialDir = initialDir.GetNormal();
        var a2 = endRadius * length;
        var n = Math.Max(4, samples);
        for (var i = 0; i <= n; i++)
        {
            var s = length * i / n;
            EvaluateFresnel(s, a2, out var x, out var y);
            y *= sideSign;
            var world = start + new Vector2d(
                initialDir.X * x - initialDir.Y * y,
                initialDir.Y * x + initialDir.X * y);
            pts.Add(world);
        }

        return pts;
    }

    private static Point2d RotateAround(Point2d point, Point2d center, double angle) =>
        center + Rotate(point - center, angle);

    private static void EvaluateFresnel(double s, double a2, out double x, out double y)
    {
        if (s <= Tol)
        {
            x = 0;
            y = 0;
            return;
        }

        var tau = s * s / (2.0 * a2);
        var tau2 = tau * tau;
        var tau3 = tau2 * tau;
        var tau4 = tau2 * tau2;
        var tau5 = tau4 * tau;
        x = s * (1.0 - tau2 / 10.0 + tau4 / 216.0);
        y = s * (tau / 3.0 - tau3 / 42.0 + tau5 / 1320.0);
    }

    private static Vector2d Rotate(Vector2d v, double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        return new Vector2d(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
