using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class TangentNodeGeometry
{
    public static IReadOnlyList<TangentNodeInfo> Collect(RoadAxis axis)
    {
        var nodes = new List<TangentNodeInfo>();
        if (axis.Elements.Count < 3)
        {
            return nodes;
        }

        var number = 1;
        for (var i = 1; i < axis.Elements.Count - 1; i++)
        {
            var arc = axis.Elements[i];
            var prev = axis.Elements[i - 1];
            var next = axis.Elements[i + 1];
            if (arc.Type != AlignmentElementType.Arc ||
                prev.Type != AlignmentElementType.Tangent ||
                next.Type != AlignmentElementType.Tangent ||
                arc.Radius < 1e-6)
            {
                continue;
            }

            if (!TryCompute(prev, arc, next, number, i, out var node))
            {
                continue;
            }

            nodes.Add(node);
            number++;
        }

        return nodes;
    }

    public static bool TryCompute(
        AlignmentElement prevTangent,
        AlignmentElement arc,
        AlignmentElement nextTangent,
        int number,
        int arcElementIndex,
        out TangentNodeInfo node)
    {
        node = null!;
        var p0 = TangentArcGeometry.To2d(prevTangent.Start);
        var p1 = TangentArcGeometry.To2d(prevTangent.End);
        var p2 = TangentArcGeometry.To2d(nextTangent.Start);
        var p3 = TangentArcGeometry.To2d(nextTangent.End);
        if (!TangentArcGeometry.TryIntersectLines(p0, p1, p2, p3, out var pi2d))
        {
            return false;
        }

        var inDir = (p1 - p0);
        var outDir = (p3 - p2);
        if (inDir.Length < TangentArcGeometry.MinSegmentLength ||
            outDir.Length < TangentArcGeometry.MinSegmentLength)
        {
            return false;
        }

        inDir = inDir.GetNormal();
        outDir = outDir.GetNormal();
        var dot = MathNet48.Clamp(inDir.DotProduct(outDir), -1.0, 1.0);
        var deflection = Math.Acos(dot);
        if (deflection < 0.001 || Math.Abs(deflection - Math.PI) < 0.001)
        {
            return false;
        }

        var radius = Math.Max(arc.Radius, TangentArcGeometry.MinSegmentLength);
        var half = deflection / 2.0;
        var tangentLength = radius * Math.Tan(half);
        // Ako luk skraćuje T zbog dostupnog prostora, uzmi stvarni PC–PI / PI–PT.
        var t1 = Distance2d(pi2d, TangentArcGeometry.To2d(arc.Start));
        var t2 = Distance2d(pi2d, TangentArcGeometry.To2d(arc.End));
        if (t1 < TangentArcGeometry.MinSegmentLength)
        {
            t1 = tangentLength;
        }

        if (t2 < TangentArcGeometry.MinSegmentLength)
        {
            t2 = tangentLength;
        }

        var cosHalf = Math.Cos(half);
        var external = cosHalf > 1e-9
            ? radius * (1.0 / cosHalf - 1.0)
            : 0.0;

        // Bisektrisa ka unutra (smer ka centru luka / ka PC–PT uglu π−α).
        // Tabela mora ići NA SPOLJNU stranu krivine (nasuprot centru) — kao na referentnim crtežima.
        var toPc = inDir.Negate();
        var toPt = outDir;
        var towardArc = new Vector2d(toPc.X + toPt.X, toPc.Y + toPt.Y);
        if (towardArc.Length < 1e-9)
        {
            // Fallback: od PI ka centru luka.
            var center2d = TangentArcGeometry.To2d(arc.Center);
            towardArc = new Vector2d(center2d.X - pi2d.X, center2d.Y - pi2d.Y);
        }

        if (towardArc.Length < 1e-9)
        {
            towardArc = new Vector2d(-inDir.Y, inDir.X);
        }

        towardArc = towardArc.GetNormal();

        // Dodatna provera: smer ka centru luka mora biti istog znaka kao towardArc.
        var centerDelta = TangentArcGeometry.To2d(arc.Center) - pi2d;
        if (centerDelta.Length > 1e-9 && towardArc.DotProduct(centerDelta) < 0)
        {
            towardArc = towardArc.Negate();
        }

        var exterior = towardArc.Negate();

        node = new TangentNodeInfo
        {
            Number = number,
            ArcElementIndex = arcElementIndex,
            Pi = TangentArcGeometry.To3d(pi2d),
            DeflectionRadians = deflection,
            Radius = radius,
            ArcLength = Math.Max(arc.Length, radius * deflection),
            TangentLength1 = t1,
            TangentLength2 = t2,
            ExternalDistance = external,
            OpenBisector = new Vector3d(exterior.X, exterior.Y, 0)
        };
        return true;
    }

    private static double Distance2d(Point2d a, Point2d b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
