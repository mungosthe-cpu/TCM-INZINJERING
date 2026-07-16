using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Jedan trougao TIN-a sa referencom na 3DFACE (za bojenje / analizu).</summary>
internal readonly struct TerrainFacePart
{
    public ObjectId FaceId { get; }
    public TerrainTriangle Triangle { get; }

    public TerrainFacePart(ObjectId faceId, TerrainTriangle triangle)
    {
        FaceId = faceId;
        Triangle = triangle;
    }
}

/// <summary>Rezultat slope analitike za jedan trougao.</summary>
internal readonly struct TerrainSlopeSample
{
    public Point3d Centroid { get; }
    /// <summary>Nagib u procentima (rise/run × 100). 0 = ravno.</summary>
    public double SlopePercent { get; }
    /// <summary>Jedinični vektor u XY u smeru najstrmijeg spusta (padina).</summary>
    public Vector2d FlowDirection { get; }
    public bool IsFlat { get; }

    public TerrainSlopeSample(Point3d centroid, double slopePercent, Vector2d flowDirection, bool isFlat)
    {
        Centroid = centroid;
        SlopePercent = slopePercent;
        FlowDirection = flowDirection;
        IsFlat = isFlat;
    }
}

internal static class TerrainSlopeMath
{
    /// <summary>
    /// Ravan Ax+By+Cz+D=0 → gradient Z u XY: (dz/dx, dz/dy) = (-a/c, -b/c) za c≠0.
    /// Slope% = |grad| × 100; flow = -grad / |grad|.
    /// </summary>
    public static TerrainSlopeSample Analyze(TerrainTriangle t)
    {
        var centroid = new Point3d(
            (t.A.X + t.B.X + t.C.X) / 3.0,
            (t.A.Y + t.B.Y + t.C.Y) / 3.0,
            (t.A.Z + t.B.Z + t.C.Z) / 3.0);

        var ab = t.B - t.A;
        var ac = t.C - t.A;
        var n = ab.CrossProduct(ac);
        if (n.Length < 1e-18)
        {
            return new TerrainSlopeSample(centroid, 0, new Vector2d(0, 0), isFlat: true);
        }

        // Normala ka „gore“ (pozitivan Z).
        if (n.Z < 0)
        {
            n = -n;
        }

        if (Math.Abs(n.Z) < 1e-12)
        {
            // Vertikalni zid — max slope, flow horizontal duž -n_xy.
            var fx = -n.X;
            var fy = -n.Y;
            var len = Math.Sqrt(fx * fx + fy * fy);
            var dir = len < 1e-18
                ? new Vector2d(0, 0)
                : new Vector2d(fx / len, fy / len);
            return new TerrainSlopeSample(centroid, 10_000, dir, isFlat: false);
        }

        var dzdx = -n.X / n.Z;
        var dzdy = -n.Y / n.Z;
        var gradLen = Math.Sqrt(dzdx * dzdx + dzdy * dzdy);
        if (gradLen < 1e-12)
        {
            return new TerrainSlopeSample(centroid, 0, new Vector2d(0, 0), isFlat: true);
        }

        // Flow downhill = -grad.
        var flow = new Vector2d(-dzdx / gradLen, -dzdy / gradLen);
        var slopePct = gradLen * 100.0;
        return new TerrainSlopeSample(centroid, slopePct, flow, isFlat: false);
    }

    /// <summary>Civil-like ACI opsezi za nagib (%).</summary>
    public static short SlopePercentToAci(double slopePercent) =>
        slopePercent switch
        {
            < 2 => 150,   // plavo — blago
            < 5 => 140,   // cyan
            < 10 => 80,   // zeleno
            < 15 => 50,   // žuto
            < 25 => 30,   // narandžasto
            < 40 => 20,   // crveno
            _ => 1        // jarko crveno — strmo
        };

    public static short ElevationBandToAci(double z, double zMin, double zMax)
    {
        if (zMax - zMin < 1e-9)
        {
            return 3;
        }

        var t = (z - zMin) / (zMax - zMin);
        return t switch
        {
            < 0.2 => 150,
            < 0.4 => 140,
            < 0.6 => 80,
            < 0.8 => 50,
            _ => 1
        };
    }

    public static short BasinIdToAci(int basinId)
    {
        ReadOnlySpan<short> palette = [1, 3, 4, 5, 6, 2, 30, 40, 80, 140, 150, 200];
        if (basinId < 0)
        {
            return 8;
        }

        return palette[basinId % palette.Length];
    }
}
