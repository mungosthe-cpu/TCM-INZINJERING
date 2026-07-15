using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal sealed class TerrainTriangle
{
    public Point3d A { get; }
    public Point3d B { get; }
    public Point3d C { get; }

    public double MinX { get; }
    public double MaxX { get; }
    public double MinY { get; }
    public double MaxY { get; }

    public TerrainTriangle(Point3d a, Point3d b, Point3d c)
    {
        A = a;
        B = b;
        C = c;
        MinX = Math.Min(a.X, Math.Min(b.X, c.X));
        MaxX = Math.Max(a.X, Math.Max(b.X, c.X));
        MinY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
        MaxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
    }

    /// <summary>
    /// Vertikalni hit: da li (x,y) pada u XY projekciju trougla, i koja je Z.
    /// </summary>
    public bool TryGetElevation(double x, double y, out double z)
    {
        z = 0;
        if (x < MinX - 1e-9 || x > MaxX + 1e-9 || y < MinY - 1e-9 || y > MaxY + 1e-9)
        {
            return false;
        }

        var v0x = C.X - A.X;
        var v0y = C.Y - A.Y;
        var v1x = B.X - A.X;
        var v1y = B.Y - A.Y;
        var v2x = x - A.X;
        var v2y = y - A.Y;

        var dot00 = v0x * v0x + v0y * v0y;
        var dot01 = v0x * v1x + v0y * v1y;
        var dot02 = v0x * v2x + v0y * v2y;
        var dot11 = v1x * v1x + v1y * v1y;
        var dot12 = v1x * v2x + v1y * v2y;

        var denom = dot00 * dot11 - dot01 * dot01;
        if (Math.Abs(denom) < 1e-18)
        {
            return false;
        }

        var inv = 1.0 / denom;
        var u = (dot11 * dot02 - dot01 * dot12) * inv;
        var v = (dot00 * dot12 - dot01 * dot02) * inv;
        if (u < -1e-8 || v < -1e-8 || u + v > 1.0 + 1e-8)
        {
            return false;
        }

        z = A.Z + v * (B.Z - A.Z) + u * (C.Z - A.Z);
        return true;
    }

    public IEnumerable<(Point2d P, Point2d Q)> GetPlanEdges()
    {
        yield return (new Point2d(A.X, A.Y), new Point2d(B.X, B.Y));
        yield return (new Point2d(B.X, B.Y), new Point2d(C.X, C.Y));
        yield return (new Point2d(C.X, C.Y), new Point2d(A.X, A.Y));
    }
}
