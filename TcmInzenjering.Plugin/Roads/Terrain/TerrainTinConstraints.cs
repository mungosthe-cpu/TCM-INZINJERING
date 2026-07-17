using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Breakline enforcement (edge flips), boundary clip, i mergovanje tacaka iz polilinija.
/// </summary>
internal static class TerrainTinConstraints
{
    private const double Tol = 1e-6;

    public sealed class BuildResult
    {
        public required List<Point3d> Vertices { get; init; }
        public required List<TerrainDelaunay.Triangle> Triangles { get; init; }
        public int BreaklineSegments { get; init; }
        public int ForcedApplied { get; init; }
        public int DeletedRemoved { get; init; }
        public int BoundaryCulled { get; init; }
    }

    public static BuildResult Build(
        Transaction tr,
        Database db,
        IReadOnlyList<Point3d> seedPoints,
        IReadOnlyList<(Point3d A, Point3d B)>? extraForcedEdges = null)
    {
        var verts = Deduplicate(seedPoints);
        var breakSegs = new List<(Point3d A, Point3d B)>();
        var outerRings = new List<List<Point2d>>();
        var hideRings = new List<List<Point2d>>();

        foreach (var handle in TerrainDefinitionStore.LoadBreaklineHandles(tr, db))
        {
            if (!TryGetCurve(tr, db, handle, out var curve) || curve is null)
            {
                continue;
            }

            var polyPts = SampleCurvePoints(tr, curve);
            MergePoints(verts, polyPts);
            for (var i = 0; i + 1 < polyPts.Count; i++)
            {
                if (XyDist(polyPts[i], polyPts[i + 1]) > Tol)
                {
                    breakSegs.Add((polyPts[i], polyPts[i + 1]));
                }
            }
        }

        foreach (var boundary in TerrainDefinitionStore.LoadBoundaries(tr, db))
        {
            if (!TryGetCurve(tr, db, boundary.Handle, out var curve) || curve is null)
            {
                continue;
            }

            var polyPts = SampleCurvePoints(tr, curve);
            if (polyPts.Count < 3)
            {
                continue;
            }

            MergePoints(verts, polyPts);
            var ring = polyPts.Select(p => new Point2d(p.X, p.Y)).ToList();
            if (boundary.Kind == TerrainBoundaryKind.Outer)
            {
                outerRings.Add(ring);
            }
            else
            {
                hideRings.Add(ring);
            }
        }

        verts = Deduplicate(verts);
        var tris = TerrainDelaunay.Triangulate(verts).ToList();
        if (tris.Count == 0)
        {
            return new BuildResult
            {
                Vertices = verts,
                Triangles = tris,
                BreaklineSegments = breakSegs.Count
            };
        }

        var index = BuildIndex(verts);
        var forcedApplied = 0;
        foreach (var seg in breakSegs)
        {
            if (TryGetIndex(index, seg.A, out var i) && TryGetIndex(index, seg.B, out var j) && i != j)
            {
                if (EnforceEdge(tris, verts, i, j))
                {
                    forcedApplied++;
                }
            }
        }

        foreach (var edge in TerrainDefinitionStore.LoadForcedEdges(tr, db))
        {
            if (TryGetIndex(index, edge.A, out var i) && TryGetIndex(index, edge.B, out var j) && i != j)
            {
                if (EnforceEdge(tris, verts, i, j))
                {
                    forcedApplied++;
                }
            }
        }

        if (extraForcedEdges is not null)
        {
            foreach (var edge in extraForcedEdges)
            {
                if (TryGetIndex(index, edge.A, out var i) &&
                    TryGetIndex(index, edge.B, out var j) &&
                    i != j &&
                    EnforceEdge(tris, verts, i, j))
                {
                    forcedApplied++;
                }
            }
        }

        var beforeDelete = tris.Count;
        foreach (var edge in TerrainDefinitionStore.LoadDeletedEdges(tr, db))
        {
            if (TryGetIndex(index, edge.A, out var i) && TryGetIndex(index, edge.B, out var j) && i != j)
            {
                tris.RemoveAll(t => TriangleHasEdge(t, i, j));
            }
        }

        var deletedRemoved = beforeDelete - tris.Count;

        var beforeClip = tris.Count;
        if (outerRings.Count > 0 || hideRings.Count > 0)
        {
            tris = tris.Where(t =>
            {
                var c = Centroid(verts[t.A], verts[t.B], verts[t.C]);
                if (outerRings.Count > 0 && outerRings.All(r => !PointInRing(c, r)))
                {
                    return false;
                }

                if (hideRings.Any(r => PointInRing(c, r)))
                {
                    return false;
                }

                return true;
            }).ToList();
        }

        return new BuildResult
        {
            Vertices = verts,
            Triangles = tris,
            BreaklineSegments = breakSegs.Count,
            ForcedApplied = forcedApplied,
            DeletedRemoved = deletedRemoved,
            BoundaryCulled = beforeClip - tris.Count
        };
    }

    /// <summary>
    /// Proba da li se ivica može uključiti u TIN bez preklapanja/sečenja trouglova (Add Line).
    /// </summary>
    public static bool TryValidateForcedEdge(
        Transaction tr,
        Database db,
        IReadOnlyList<Point3d> seedPoints,
        Point3d a,
        Point3d b,
        out string? failureReason)
    {
        failureReason = null;
        if (XyDist(a, b) <= Tol)
        {
            failureReason = "Temena moraju biti razlicita.";
            return false;
        }

        var baseline = Build(tr, db, seedPoints);
        if (baseline.Triangles.Count == 0)
        {
            failureReason = "TIN mreza nije dostupna. Prvo pokrenite TCMTERFACE.";
            return false;
        }

        var index = BuildIndex(baseline.Vertices);
        if (!TryGetIndex(index, a, out var ia) || !TryGetIndex(index, b, out var ib))
        {
            failureReason = "Temena moraju biti postojeca TIN temena.";
            return false;
        }

        if (ia == ib)
        {
            failureReason = "Temena moraju biti razlicita.";
            return false;
        }

        if (baseline.Triangles.Any(t => TriangleHasEdge(t, ia, ib)))
        {
            return IsValidTriangulation(baseline.Triangles, baseline.Vertices);
        }

        var trial = Build(tr, db, seedPoints, extraForcedEdges: new[] { (a, b) });
        if (trial.Triangles.Count == 0)
        {
            failureReason =
                "Add Line nije moguce: triangulacija ne moze da se izgradi sa novom ivicom.";
            return false;
        }

        index = BuildIndex(trial.Vertices);
        if (!TryGetIndex(index, a, out ia) || !TryGetIndex(index, b, out ib))
        {
            failureReason = "Temena moraju biti postojeca TIN temena.";
            return false;
        }

        if (!trial.Triangles.Any(t => TriangleHasEdge(t, ia, ib)))
        {
            failureReason =
                "Add Line nije moguce: nova TIN ivica ne moze da se ukljuci u mrezu bez preklapanja ili secenja 3DFACE trouglova.\n\n" +
                "Izaberite druga dva temena ili prvo izmenite okolne ivice (Swap / Delete).";
            return false;
        }

        if (!IsValidTriangulation(trial.Triangles, trial.Vertices))
        {
            failureReason =
                "Add Line nije moguce: rezultujuci 3DFACE trouglovi bi se secili ili preklapali.\n\n" +
                "Izaberite druga dva temena ili prvo izmenite okolne ivice (Swap / Delete).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validacija više segmenata (Add Line duž linije).
    /// </summary>
    public static bool TryValidateForcedEdges(
        Transaction tr,
        Database db,
        IReadOnlyList<Point3d> seedPoints,
        IReadOnlyList<(Point3d A, Point3d B)> edges,
        out string? failureReason)
    {
        failureReason = null;
        var unique = edges
            .Where(e => XyDist(e.A, e.B) > Tol)
            .ToList();
        if (unique.Count == 0)
        {
            failureReason = "Nema segmenata za Add Line.";
            return false;
        }

        var trial = Build(tr, db, seedPoints, extraForcedEdges: unique);
        if (trial.Triangles.Count == 0)
        {
            failureReason =
                "Add Line nije moguce: triangulacija ne moze da se izgradi sa novim ivicama.";
            return false;
        }

        if (!IsValidTriangulation(trial.Triangles, trial.Vertices))
        {
            failureReason =
                "Add Line nije moguce: rezultujuci 3DFACE trouglovi bi se secili ili preklapali.\n\n" +
                "Proverite putanju linije i okolne TIN ivice.";
            return false;
        }

        var index = BuildIndex(trial.Vertices);
        foreach (var edge in unique)
        {
            if (!TryGetIndex(index, edge.A, out var ia) ||
                !TryGetIndex(index, edge.B, out var ib) ||
                !trial.Triangles.Any(t => TriangleHasEdge(t, ia, ib)))
            {
                failureReason =
                    "Add Line nije moguce: jedna ili vise novih ivica ne moze da se ukljuci u TIN mrezu bez preklapanja trouglova.\n\n" +
                    "Proverite putanju linije i okolne TIN ivice.";
                return false;
            }
        }

        return true;
    }

    private static bool IsValidTriangulation(
        IReadOnlyList<TerrainDelaunay.Triangle> tris,
        IReadOnlyList<Point3d> verts)
    {
        foreach (var tri in tris)
        {
            if (Area2(verts[tri.A], verts[tri.B], verts[tri.C]) <= 1e-12)
            {
                return false;
            }
        }

        var edges = new List<(int I, int J)>(tris.Count * 3);
        foreach (var tri in tris)
        {
            edges.Add(NormalizeEdge(tri.A, tri.B));
            edges.Add(NormalizeEdge(tri.B, tri.C));
            edges.Add(NormalizeEdge(tri.C, tri.A));
        }

        for (var i = 0; i < edges.Count; i++)
        {
            var (a1, b1) = edges[i];
            var p1 = verts[a1];
            var p2 = verts[b1];
            for (var j = i + 1; j < edges.Count; j++)
            {
                var (a2, b2) = edges[j];
                if (a1 == a2 || a1 == b2 || b1 == a2 || b1 == b2)
                {
                    continue;
                }

                if (SegmentsProperIntersect(p1, p2, verts[a2], verts[b2]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static (int I, int J) NormalizeEdge(int a, int b) => a < b ? (a, b) : (b, a);

    private static bool EnforceEdge(List<TerrainDelaunay.Triangle> tris, List<Point3d> verts, int i, int j)
    {
        for (var iter = 0; iter < 300; iter++)
        {
            if (tris.Any(t => TriangleHasEdge(t, i, j)))
            {
                return true;
            }

            if (!TryFlipAnyCrossing(tris, verts, i, j))
            {
                return false;
            }
        }

        return tris.Any(t => TriangleHasEdge(t, i, j));
    }

    private static bool TryFlipAnyCrossing(List<TerrainDelaunay.Triangle> tris, List<Point3d> verts, int i, int j)
    {
        var pi = verts[i];
        var pj = verts[j];
        for (var t0 = 0; t0 < tris.Count; t0++)
        {
            var tri = tris[t0];
            var edges = new[] { (tri.A, tri.B), (tri.B, tri.C), (tri.C, tri.A) };
            foreach (var (a, b) in edges)
            {
                if (a == i || a == j || b == i || b == j)
                {
                    continue;
                }

                if (!SegmentsProperIntersect(pi, pj, verts[a], verts[b]))
                {
                    continue;
                }

                // Pronadji susedni trougao koji deli ab.
                for (var t1 = 0; t1 < tris.Count; t1++)
                {
                    if (t1 == t0 || !TriangleHasEdge(tris[t1], a, b))
                    {
                        continue;
                    }

                    if (TryFlip(tris, verts, t0, t1, a, b))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryFlip(
        List<TerrainDelaunay.Triangle> tris,
        List<Point3d> verts,
        int t0,
        int t1,
        int a,
        int b)
    {
        var c = OppositeVertex(tris[t0], a, b);
        var d = OppositeVertex(tris[t1], a, b);
        if (c < 0 || d < 0 || c == d)
        {
            return false;
        }

        // Convex quad check: diagonals ab and cd must intersect (or cd is legal swap).
        if (!SegmentsProperIntersect(verts[a], verts[b], verts[c], verts[d]) &&
            !IsConvexQuad(verts[a], verts[b], verts[c], verts[d]))
        {
            // Allow flip if both triangles form convex quad around cd.
            if (!IsConvexQuad(verts[a], verts[c], verts[b], verts[d]))
            {
                return false;
            }
        }

        tris[t0] = new TerrainDelaunay.Triangle(a, c, d);
        tris[t1] = new TerrainDelaunay.Triangle(b, c, d);
        return true;
    }

    private static int OppositeVertex(TerrainDelaunay.Triangle t, int a, int b)
    {
        if (t.A != a && t.A != b)
        {
            return t.A;
        }

        if (t.B != a && t.B != b)
        {
            return t.B;
        }

        if (t.C != a && t.C != b)
        {
            return t.C;
        }

        return -1;
    }

    private static bool TriangleHasEdge(TerrainDelaunay.Triangle t, int i, int j) =>
        (t.A == i && t.B == j) || (t.B == i && t.A == j) ||
        (t.B == i && t.C == j) || (t.C == i && t.B == j) ||
        (t.C == i && t.A == j) || (t.A == i && t.C == j);

    private static bool IsConvexQuad(Point3d a, Point3d b, Point3d c, Point3d d)
    {
        // Rough: both triangles non-degenerate and diagonals share interior.
        return Area2(a, c, d) > 1e-12 && Area2(b, c, d) > 1e-12;
    }

    private static double Area2(Point3d a, Point3d b, Point3d c) =>
        Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));

    private static bool SegmentsProperIntersect(Point3d a, Point3d b, Point3d c, Point3d d)
    {
        var o1 = Orient(a, b, c);
        var o2 = Orient(a, b, d);
        var o3 = Orient(c, d, a);
        var o4 = Orient(c, d, b);
        return o1 * o2 < 0 && o3 * o4 < 0;
    }

    private static double Orient(Point3d a, Point3d b, Point3d c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static Point2d Centroid(Point3d a, Point3d b, Point3d c) =>
        new((a.X + b.X + c.X) / 3.0, (a.Y + b.Y + c.Y) / 3.0);

    private static bool PointInRing(Point2d p, IReadOnlyList<Point2d> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var pi = ring[i];
            var pj = ring[j];
            var intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                            (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / ((pj.Y - pi.Y) + 1e-30) + pi.X);
            if (intersect)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static List<Point3d> SampleCurvePoints(Transaction tr, Curve curve)
    {
        var pts = new List<Point3d>();
        switch (curve)
        {
            case Polyline pl:
                for (var i = 0; i < pl.NumberOfVertices; i++)
                {
                    pts.Add(pl.GetPoint3dAt(i));
                }

                if (pl.Closed && pts.Count > 2)
                {
                    pts.Add(pts[0]);
                }

                break;
            case Polyline3d pl3:
            {
                foreach (ObjectId id in pl3)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is PolylineVertex3d v)
                    {
                        pts.Add(v.Position);
                    }
                }

                break;
            }
            default:
            {
                var start = curve.StartParam;
                var end = curve.EndParam;
                const int samples = 32;
                for (var s = 0; s <= samples; s++)
                {
                    var t = start + (end - start) * s / samples;
                    pts.Add(curve.GetPointAtParameter(t));
                }

                break;
            }
        }

        return pts;
    }

    private static bool TryGetCurve(Transaction tr, Database db, long handleValue, out Curve? curve)
    {
        curve = null;
        try
        {
            var handle = new Handle(handleValue);
            if (!db.TryGetObjectId(handle, out var id) || id.IsNull)
            {
                return false;
            }

            curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
            return curve is not null && !curve.IsErased;
        }
        catch
        {
            return false;
        }
    }

    private static void MergePoints(List<Point3d> dest, IReadOnlyList<Point3d> src)
    {
        foreach (var p in src)
        {
            var found = false;
            for (var i = 0; i < dest.Count; i++)
            {
                if (Math.Abs(dest[i].X - p.X) <= Tol && Math.Abs(dest[i].Y - p.Y) <= Tol)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                dest.Add(p);
            }
        }
    }

    private static List<Point3d> Deduplicate(IReadOnlyList<Point3d> points)
    {
        var unique = new List<Point3d>(points.Count);
        foreach (var p in points)
        {
            var dup = false;
            for (var i = 0; i < unique.Count; i++)
            {
                if (Math.Abs(unique[i].X - p.X) <= Tol && Math.Abs(unique[i].Y - p.Y) <= Tol)
                {
                    dup = true;
                    break;
                }
            }

            if (!dup)
            {
                unique.Add(p);
            }
        }

        return unique;
    }

    private static Dictionary<(long, long), int> BuildIndex(List<Point3d> verts)
    {
        var map = new Dictionary<(long, long), int>();
        for (var i = 0; i < verts.Count; i++)
        {
            map[Quantize(verts[i])] = i;
        }

        return map;
    }

    private static bool TryGetIndex(Dictionary<(long, long), int> map, Point3d p, out int index) =>
        map.TryGetValue(Quantize(p), out index);

    private static (long, long) Quantize(Point3d p) =>
        ((long)Math.Round(p.X / Tol), (long)Math.Round(p.Y / Tol));

    private static double XyDist(Point3d a, Point3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
