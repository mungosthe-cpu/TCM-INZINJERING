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
        /// <summary>Nove tačke sa granice (preseci + temena sa interpolisanom Z) — odvojeno od baznog terena.</summary>
        public int ConstraintPointsAdded { get; init; }
        /// <summary>Tačke granice za snimanje u *_Granica (bez baznih seed tačaka).</summary>
        public IReadOnlyList<Point3d> BoundaryPoints { get; init; } = Array.Empty<Point3d>();
        /// <summary>Forsirane ivice (iz store + extra) koje swap nije uspeo da ugradi u TIN.</summary>
        public IReadOnlyList<(Point3d A, Point3d B)> FailedForcedEdges { get; init; } =
            Array.Empty<(Point3d A, Point3d B)>();
    }

    public static BuildResult Build(
        Transaction tr,
        Database db,
        IReadOnlyList<Point3d> seedPoints,
        IReadOnlyList<(Point3d A, Point3d B)>? extraForcedEdges = null,
        IProgress<(int Percent, string Status)>? progress = null)
    {
        progress?.Report((55, "Pripremam tacke i ogranicenja…"));
        var seedCount = Deduplicate(seedPoints).Count;
        var verts = Deduplicate(seedPoints);
        var breakSegs = new List<(Point3d A, Point3d B)>();
        var outerRings = new List<List<Point2d>>();
        var hideRings = new List<List<Point2d>>();
        var boundaryCollector = new List<Point3d>();

        // Početna mreža samo od terenskih tačaka — za interpolaciju Z na 2D granici/breakline.
        var seedElevator = BuildElevationLookup(verts);

        foreach (var handle in TerrainDefinitionStore.LoadBreaklineHandles(tr, db))
        {
            if (!TryGetCurve(tr, db, handle, out var curve) || curve is null)
            {
                continue;
            }

            var polyPts = SampleCurvePoints(tr, curve);
            AbsorbConstraintPolyline(
                verts, seedElevator, polyPts, breakSegs, closeRing: false, boundaryCollector: null);
        }

        foreach (var boundary in TerrainDefinitionStore.LoadBoundaries(tr, db))
        {
            if (!TryGetCurve(tr, db, boundary.Handle, out var curve) || curve is null)
            {
                continue;
            }

            var polyPts = SampleCurvePoints(tr, curve);
            if (polyPts.Count < 2)
            {
                continue;
            }

            // Granica: temena + preseci; Z sa terena; sakupi u BoundaryPoints.
            AbsorbConstraintPolyline(
                verts, seedElevator, polyPts, breakSegs, closeRing: true, boundaryCollector);

            var ring = ElevatePolyline(polyPts, seedElevator)
                .Select(p => new Point2d(p.X, p.Y))
                .ToList();
            if (ring.Count < 3)
            {
                continue;
            }

            if (boundary.Kind == TerrainBoundaryKind.Outer)
            {
                outerRings.Add(ring);
            }
            else
            {
                hideRings.Add(ring);
            }
        }

        var boundaryPoints = Deduplicate(boundaryCollector);
        var constraintAdded = Math.Max(0, verts.Count - seedCount);
        // Broj stvarno novih granica-tačaka (ne breakline).
        constraintAdded = boundaryPoints.Count;

        progress?.Report((60, "Delaunay triangulacija…"));
        verts = Deduplicate(verts);
        var tris = TerrainDelaunay.Triangulate(verts).ToList();
        if (tris.Count == 0)
        {
            return new BuildResult
            {
                Vertices = verts,
                Triangles = tris,
                BreaklineSegments = breakSegs.Count,
                ConstraintPointsAdded = constraintAdded,
                BoundaryPoints = boundaryPoints
            };
        }

        var index = BuildIndex(verts);
        var forcedApplied = 0;
        var breakTotal = breakSegs.Count;
        for (var bi = 0; bi < breakSegs.Count; bi++)
        {
            var seg = breakSegs[bi];
            if (breakTotal > 0 && bi % Math.Max(1, breakTotal / 20) == 0)
            {
                var pct = 62 + (int)(8.0 * (bi + 1) / breakTotal);
                progress?.Report((pct, $"Breakline swap {bi + 1}/{breakTotal}…"));
            }

            forcedApplied += EnforceEdgeChain(tris, verts, index, seg.A, seg.B);
        }

        var forcedEdges = TerrainDefinitionStore.LoadForcedEdges(tr, db);
        var forcedTotal = forcedEdges.Count;
        for (var fi = 0; fi < forcedEdges.Count; fi++)
        {
            var edge = forcedEdges[fi];
            if (forcedTotal > 0 && (fi % Math.Max(1, forcedTotal / 25) == 0 || fi + 1 == forcedTotal))
            {
                var pct = 70 + (int)(18.0 * (fi + 1) / forcedTotal);
                progress?.Report((pct, $"Forsiram ivice (swap) {fi + 1}/{forcedTotal}…"));
            }

            forcedApplied += EnforceEdgeChain(tris, verts, index, edge.A, edge.B);
        }

        if (extraForcedEdges is not null)
        {
            foreach (var edge in extraForcedEdges)
            {
                forcedApplied += EnforceEdgeChain(tris, verts, index, edge.A, edge.B);
            }
        }

        // Poslednja zaštita: ako je mreža i dalje sa sečenjem, popravi ili vrati Delaunay.
        if (!IsValidTriangulation(tris, verts))
        {
            progress?.Report((88, "Popravljam preklopene trouglove…"));
            if (!TryRepairCrossingEdges(tris, verts))
            {
                progress?.Report((89, "Reset TIN na Delaunay (fors. ivice strogo)…"));
                tris = TerrainDelaunay.Triangulate(verts).ToList();
                forcedApplied = 0;
                foreach (var seg in breakSegs)
                {
                    forcedApplied += EnforceEdgeChain(tris, verts, index, seg.A, seg.B);
                }

                foreach (var edge in forcedEdges)
                {
                    forcedApplied += EnforceEdgeChain(tris, verts, index, edge.A, edge.B);
                }

                if (extraForcedEdges is not null)
                {
                    foreach (var edge in extraForcedEdges)
                    {
                        forcedApplied += EnforceEdgeChain(tris, verts, index, edge.A, edge.B);
                    }
                }

                if (!IsValidTriangulation(tris, verts))
                {
                    tris = TerrainDelaunay.Triangulate(verts).ToList();
                    forcedApplied = 0;
                }
            }
        }

        // Evidencija ivica koje nisu ugrađene — pre clip-a granice (clip nije neuspeh swap-a).
        var failedForced = new List<(Point3d A, Point3d B)>();
        foreach (var edge in forcedEdges)
        {
            if (!IsEdgeChainPresent(tris, verts, index, edge.A, edge.B))
            {
                failedForced.Add((edge.A, edge.B));
            }
        }

        if (extraForcedEdges is not null)
        {
            foreach (var edge in extraForcedEdges)
            {
                if (!IsEdgeChainPresent(tris, verts, index, edge.A, edge.B))
                {
                    failedForced.Add((edge.A, edge.B));
                }
            }
        }

        progress?.Report((90, "Primenjujem delete / granicu…"));
        var beforeDelete = tris.Count;
        // Jedan prolaz: skup obrisanih ivica → filtriranje trouglova O(D+T).
        var deletedPairs = new HashSet<(int, int)>();
        foreach (var edge in TerrainDefinitionStore.LoadDeletedEdges(tr, db))
        {
            if (TryGetIndex(index, edge.A, out var i) &&
                TryGetIndex(index, edge.B, out var j) &&
                i != j)
            {
                deletedPairs.Add(i < j ? (i, j) : (j, i));
            }
        }

        if (deletedPairs.Count > 0)
        {
            tris = tris.Where(t =>
            {
                var e01 = t.A < t.B ? (t.A, t.B) : (t.B, t.A);
                var e12 = t.B < t.C ? (t.B, t.C) : (t.C, t.B);
                var e20 = t.C < t.A ? (t.C, t.A) : (t.A, t.C);
                return !deletedPairs.Contains(e01) &&
                       !deletedPairs.Contains(e12) &&
                       !deletedPairs.Contains(e20);
            }).ToList();
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

        progress?.Report((93, "TIN mreza spremna…"));
        return new BuildResult
        {
            Vertices = verts,
            Triangles = tris,
            BreaklineSegments = breakSegs.Count,
            ForcedApplied = forcedApplied,
            DeletedRemoved = deletedRemoved,
            BoundaryCulled = beforeClip - tris.Count,
            ConstraintPointsAdded = constraintAdded,
            BoundaryPoints = boundaryPoints,
            FailedForcedEdges = failedForced
        };
    }

    /// <summary>Da li je forsirana ivica (kroz eventualna međutemena) prisutna u TIN-u.</summary>
    private static bool IsEdgeChainPresent(
        List<TerrainDelaunay.Triangle> tris,
        List<Point3d> verts,
        Dictionary<(long, long), int> index,
        Point3d a,
        Point3d b)
    {
        if (!TryGetIndex(index, a, out var ia) || !TryGetIndex(index, b, out var ib) || ia == ib)
        {
            // Tačke nisu u mreži — tretiraj kao neuspeh samo ako obe postoje.
            return true;
        }

        var chain = SplitEdgeThroughVertices(verts, ia, ib);
        for (var k = 0; k + 1 < chain.Count; k++)
        {
            var i = chain[k];
            var j = chain[k + 1];
            if (!tris.Any(t => TriangleHasEdge(t, i, j)))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Brzi Add Line nad već nacrtanom mrežom. Ne radi Delaunay/Build ponovo:
    /// forsira ivicu lokalnim flipovima i validira rezultat.
    /// </summary>
    public static bool TryEnforceEdgeOnExistingMesh(
        IReadOnlyList<Point3d> vertices,
        IReadOnlyList<TerrainDelaunay.Triangle> triangles,
        Point3d a,
        Point3d b,
        out List<TerrainDelaunay.Triangle> updated,
        out string? failureReason)
    {
        failureReason = null;
        updated = triangles.ToList();
        if (XyDist(a, b) <= Tol)
        {
            failureReason = "Temena moraju biti razlicita.";
            return false;
        }

        var verts = vertices.ToList();
        var index = BuildIndex(verts);
        if (!TryGetIndex(index, a, out var ia) || !TryGetIndex(index, b, out var ib))
        {
            failureReason = "Temena moraju biti postojeca TIN temena.";
            return false;
        }

        if (IsEdgeChainPresent(updated, verts, index, a, b))
        {
            return true;
        }

        if (EnforceEdgeChain(updated, verts, index, a, b) == 0 ||
            !IsEdgeChainPresent(updated, verts, index, a, b))
        {
            failureReason =
                "Add Line nije moguce: ivica ne moze lokalnim flipom da se ukljuci u mrezu.";
            updated = triangles.ToList();
            return false;
        }

        if (!IsValidTriangulation(updated, verts))
        {
            failureReason =
                "Add Line nije moguce: rezultujuci trouglovi bi se sekli ili preklapali.";
            updated = triangles.ToList();
            return false;
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

        return !TryFindCrossingEdgesFast(tris, verts, out _, out _, out _, out _);
    }

    /// <summary>
    /// Prostorni grid nad ivicama — provera ukrštanja u ~O(E) umesto O(E²).
    /// Vraća true i prvi par ivica koje se pravilno seku.
    /// </summary>
    private static bool TryFindCrossingEdgesFast(
        IReadOnlyList<TerrainDelaunay.Triangle> tris,
        IReadOnlyList<Point3d> verts,
        out int a,
        out int b,
        out int c,
        out int d)
    {
        a = b = c = d = -1;
        var edgeSet = new HashSet<(int I, int J)>();
        foreach (var tri in tris)
        {
            edgeSet.Add(NormalizeEdge(tri.A, tri.B));
            edgeSet.Add(NormalizeEdge(tri.B, tri.C));
            edgeSet.Add(NormalizeEdge(tri.C, tri.A));
        }

        var edges = edgeSet.ToList();
        if (edges.Count < 2)
        {
            return false;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var totalLen = 0.0;
        foreach (var (i, j) in edges)
        {
            var p = verts[i];
            var q = verts[j];
            minX = Math.Min(minX, Math.Min(p.X, q.X));
            minY = Math.Min(minY, Math.Min(p.Y, q.Y));
            maxX = Math.Max(maxX, Math.Max(p.X, q.X));
            maxY = Math.Max(maxY, Math.Max(p.Y, q.Y));
            var dx = q.X - p.X;
            var dy = q.Y - p.Y;
            totalLen += Math.Sqrt(dx * dx + dy * dy);
        }

        var cell = Math.Max(totalLen / edges.Count, 1e-6);
        var grid = new Dictionary<(int Cx, int Cy), List<int>>();
        for (var e = 0; e < edges.Count; e++)
        {
            var p = verts[edges[e].I];
            var q = verts[edges[e].J];
            var x0 = (int)Math.Floor((Math.Min(p.X, q.X) - minX) / cell);
            var x1 = (int)Math.Floor((Math.Max(p.X, q.X) - minX) / cell);
            var y0 = (int)Math.Floor((Math.Min(p.Y, q.Y) - minY) / cell);
            var y1 = (int)Math.Floor((Math.Max(p.Y, q.Y) - minY) / cell);
            for (var cx = x0; cx <= x1; cx++)
            {
                for (var cy = y0; cy <= y1; cy++)
                {
                    if (!grid.TryGetValue((cx, cy), out var bucket))
                    {
                        bucket = [];
                        grid[(cx, cy)] = bucket;
                    }

                    bucket.Add(e);
                }
            }
        }

        var checkedPairs = new HashSet<long>();
        foreach (var bucket in grid.Values)
        {
            for (var m = 0; m < bucket.Count; m++)
            {
                var e1 = bucket[m];
                var (a1, b1) = edges[e1];
                var p1 = verts[a1];
                var p2 = verts[b1];
                for (var n = m + 1; n < bucket.Count; n++)
                {
                    var e2 = bucket[n];
                    var key = e1 < e2
                        ? ((long)e1 << 32) | (uint)e2
                        : ((long)e2 << 32) | (uint)e1;
                    if (!checkedPairs.Add(key))
                    {
                        continue;
                    }

                    var (a2, b2) = edges[e2];
                    if (a1 == a2 || a1 == b2 || b1 == a2 || b1 == b2)
                    {
                        continue;
                    }

                    if (SegmentsProperIntersect(p1, p2, verts[a2], verts[b2]))
                    {
                        a = a1;
                        b = b1;
                        c = a2;
                        d = b2;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static (int I, int J) NormalizeEdge(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>
    /// Forsira ivicu A–B, ali prvo deli na podivice ako postoje temena na segmentu
    /// (sprečava dugu tetivu koja seče mrežu i pravi preklapanja).
    /// </summary>
    private static int EnforceEdgeChain(
        List<TerrainDelaunay.Triangle> tris,
        List<Point3d> verts,
        Dictionary<(long, long), int> index,
        Point3d a,
        Point3d b)
    {
        if (!TryGetIndex(index, a, out var ia) || !TryGetIndex(index, b, out var ib) || ia == ib)
        {
            return 0;
        }

        var chain = SplitEdgeThroughVertices(verts, ia, ib);
        var applied = 0;
        for (var k = 0; k + 1 < chain.Count; k++)
        {
            if (TryEnforceEdgeSafe(tris, verts, chain[k], chain[k + 1]))
            {
                applied++;
            }
        }

        return applied;
    }

    /// <summary>Temena koja leže na segmentu i–j (uključujući krajeve), sortirana duž segmenta.</summary>
    private static List<int> SplitEdgeThroughVertices(List<Point3d> verts, int i, int j)
    {
        var a = verts[i];
        var b = verts[j];
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var ab2 = abx * abx + aby * aby;
        if (ab2 <= Tol * Tol)
        {
            return [i, j];
        }

        const double onSegTol = 1e-4;
        var mid = new List<(int Idx, double T)> { (i, 0), (j, 1) };
        for (var v = 0; v < verts.Count; v++)
        {
            if (v == i || v == j)
            {
                continue;
            }

            var p = verts[v];
            var t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / ab2;
            if (t <= 1e-8 || t >= 1 - 1e-8)
            {
                continue;
            }

            var projX = a.X + t * abx;
            var projY = a.Y + t * aby;
            var dx = p.X - projX;
            var dy = p.Y - projY;
            if (dx * dx + dy * dy <= onSegTol * onSegTol)
            {
                mid.Add((v, t));
            }
        }

        return mid.OrderBy(m => m.T).Select(m => m.Idx).Distinct().ToList();
    }

    /// <summary>
    /// Swap dok se ivica ne pojavi; ako ne uspe ili mreža postane nevalidna — rollback.
    /// </summary>
    private static bool TryEnforceEdgeSafe(
        List<TerrainDelaunay.Triangle> tris,
        List<Point3d> verts,
        int i,
        int j)
    {
        if (i == j)
        {
            return false;
        }

        if (tris.Any(t => TriangleHasEdge(t, i, j)))
        {
            return true;
        }

        var snapshot = tris.ToList();
        for (var iter = 0; iter < 500; iter++)
        {
            if (tris.Any(t => TriangleHasEdge(t, i, j)))
            {
                // Svaki TryFlip prihvata samo strogo konveksan četvorougao, pa flip
                // čuva validnost mreže. Skupa globalna provera ostaje jednom na kraju Build-a.
                return true;
            }

            if (!TryFlipAnyCrossing(tris, verts, i, j))
            {
                RestoreTriangles(tris, snapshot);
                return false;
            }
        }

        RestoreTriangles(tris, snapshot);
        return false;
    }

    private static void RestoreTriangles(
        List<TerrainDelaunay.Triangle> tris,
        List<TerrainDelaunay.Triangle> snapshot)
    {
        tris.Clear();
        tris.AddRange(snapshot);
    }

    /// <summary>Traži ukrštene ivice i flip-uje dok mreža ne postane validna.</summary>
    private static bool TryRepairCrossingEdges(
        List<TerrainDelaunay.Triangle> tris,
        List<Point3d> verts)
    {
        for (var pass = 0; pass < 200; pass++)
        {
            if (IsValidTriangulation(tris, verts))
            {
                return true;
            }

            if (!TryFindCrossingEdgePair(tris, verts, out var a, out var b, out var c, out var d))
            {
                return false;
            }

            var t0 = -1;
            var t1 = -1;
            for (var t = 0; t < tris.Count; t++)
            {
                if (!TriangleHasEdge(tris[t], a, b))
                {
                    continue;
                }

                if (t0 < 0)
                {
                    t0 = t;
                }
                else
                {
                    t1 = t;
                    break;
                }
            }

            if (t0 < 0 || t1 < 0)
            {
                t0 = t1 = -1;
                for (var t = 0; t < tris.Count; t++)
                {
                    if (!TriangleHasEdge(tris[t], c, d))
                    {
                        continue;
                    }

                    if (t0 < 0)
                    {
                        t0 = t;
                    }
                    else
                    {
                        t1 = t;
                        break;
                    }
                }

                if (t0 < 0 || t1 < 0 || !TryFlip(tris, verts, t0, t1, c, d))
                {
                    return false;
                }
            }
            else if (!TryFlip(tris, verts, t0, t1, a, b))
            {
                return false;
            }
        }

        return IsValidTriangulation(tris, verts);
    }

    private static bool TryFindCrossingEdgePair(
        List<TerrainDelaunay.Triangle> tris,
        List<Point3d> verts,
        out int a,
        out int b,
        out int c,
        out int d) =>
        TryFindCrossingEdgesFast(tris, verts, out a, out b, out c, out d);

    private static bool TryFlipAnyCrossing(List<TerrainDelaunay.Triangle> tris, List<Point3d> verts, int i, int j)
    {
        var pi = verts[i];
        var pj = verts[j];

        // Jednim prolazom napravi vlasnike ivica. Stara implementacija je za svaku
        // presečenu ivicu ponovo skenirala sve trouglove da nađe suseda (O(T²)).
        var owners = new Dictionary<(int I, int J), (int First, int Second)>();
        for (var t = 0; t < tris.Count; t++)
        {
            var tri = tris[t];
            var edges = new[] { (tri.A, tri.B), (tri.B, tri.C), (tri.C, tri.A) };
            foreach (var edge in edges)
            {
                var key = NormalizeEdge(edge.Item1, edge.Item2);
                if (!owners.TryGetValue(key, out var pair))
                {
                    owners[key] = (t, -1);
                }
                else if (pair.Second < 0)
                {
                    owners[key] = (pair.First, t);
                }
            }
        }

        foreach (var entry in owners)
        {
            var (ea, eb) = entry.Key;
            var (t0, t1) = entry.Value;
            if (t1 < 0 || ea == i || ea == j || eb == i || eb == j)
            {
                continue;
            }

            if (SegmentsProperIntersect(pi, pj, verts[ea], verts[eb]) &&
                TryFlip(tris, verts, t0, t1, ea, eb))
            {
                return true;
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

        // Strogo konveksan četvorougao: dijagonale ab i cd se pravilno seku.
        if (!SegmentsProperIntersect(verts[a], verts[b], verts[c], verts[d]))
        {
            return false;
        }

        if (Area2(verts[a], verts[c], verts[d]) <= 1e-12 ||
            Area2(verts[b], verts[c], verts[d]) <= 1e-12)
        {
            return false;
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

    /// <summary>
    /// Elevira 2D poliliniju, ubacuje preseke sa postojećim TIN ivicama (Z sa ivice),
    /// forsira segmente kao breakline.
    /// </summary>
    private static int AbsorbConstraintPolyline(
        List<Point3d> verts,
        SeedElevationLookup elevator,
        IReadOnlyList<Point3d> rawPoly,
        List<(Point3d A, Point3d B)> breakSegs,
        bool closeRing,
        List<Point3d>? boundaryCollector)
    {
        if (rawPoly.Count < 2)
        {
            return 0;
        }

        var elevated = ElevatePolyline(rawPoly, elevator);
        var before = verts.Count;

        // Preseci sa postojećim ivicama seed TIN-a (Z interpolisan duž terenske ivice).
        var crossings = CollectEdgeCrossings(elevator, elevated);
        foreach (var p in crossings)
        {
            MergePointPreferTerrainZ(verts, p);
            boundaryCollector?.Add(p);
        }

        foreach (var p in elevated)
        {
            MergePointPreferTerrainZ(verts, p);
            boundaryCollector?.Add(p);
        }

        // Ubaci preseke u lanac po stacionaži duž segmenata da breakline prati granicu.
        var densified = DensifyWithCrossings(elevated, crossings);
        for (var i = 0; i + 1 < densified.Count; i++)
        {
            if (XyDist(densified[i], densified[i + 1]) > Tol)
            {
                breakSegs.Add((densified[i], densified[i + 1]));
            }
        }

        if (closeRing && densified.Count >= 3 &&
            XyDist(densified[0], densified[^1]) > Tol)
        {
            breakSegs.Add((densified[^1], densified[0]));
        }

        return Math.Max(0, verts.Count - before);
    }

    private static List<Point3d> ElevatePolyline(
        IReadOnlyList<Point3d> raw,
        SeedElevationLookup elevator)
    {
        var list = new List<Point3d>(raw.Count);
        foreach (var p in raw)
        {
            list.Add(ElevatePoint(p, elevator));
        }

        return list;
    }

    private static Point3d ElevatePoint(Point3d p, SeedElevationLookup elevator)
    {
        // Uvek preferiraj Z sa postojećeg terena (2D granica često ima Elevation=0).
        if (elevator.TryGetElevation(p.X, p.Y, out var z))
        {
            return new Point3d(p.X, p.Y, z);
        }

        if (elevator.TryNearestAverage(p.X, p.Y, out var zNear))
        {
            return new Point3d(p.X, p.Y, zNear);
        }

        return p;
    }

    private static List<Point3d> CollectEdgeCrossings(
        SeedElevationLookup elevator,
        IReadOnlyList<Point3d> poly)
    {
        var crossings = new List<Point3d>();
        if (poly.Count < 2 || elevator.Edges.Count == 0)
        {
            return crossings;
        }

        for (var i = 0; i + 1 < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            if (XyDist(a, b) <= Tol)
            {
                continue;
            }

            foreach (var (e0, e1) in elevator.Edges)
            {
                if (!TryIntersectPlanSegments(
                        new Point2d(a.X, a.Y), new Point2d(b.X, b.Y),
                        new Point2d(e0.X, e0.Y), new Point2d(e1.X, e1.Y),
                        out var t, out var u))
                {
                    continue;
                }

                // Z duž TIN ivice (ne duž 2D granice).
                var z = e0.Z + u * (e1.Z - e0.Z);
                var x = a.X + t * (b.X - a.X);
                var y = a.Y + t * (b.Y - a.Y);
                crossings.Add(new Point3d(x, y, z));
            }
        }

        return crossings;
    }

    private static List<Point3d> DensifyWithCrossings(
        IReadOnlyList<Point3d> poly,
        IReadOnlyList<Point3d> crossings)
    {
        if (crossings.Count == 0)
        {
            return poly.ToList();
        }

        var result = new List<Point3d>();
        for (var i = 0; i + 1 < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            result.Add(a);
            var len2 = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
            if (len2 < Tol * Tol)
            {
                continue;
            }

            var onSeg = new List<(double T, Point3d P)>();
            foreach (var c in crossings)
            {
                var t = ((c.X - a.X) * (b.X - a.X) + (c.Y - a.Y) * (b.Y - a.Y)) / len2;
                if (t <= 1e-6 || t >= 1.0 - 1e-6)
                {
                    continue;
                }

                var px = a.X + t * (b.X - a.X);
                var py = a.Y + t * (b.Y - a.Y);
                if (Math.Abs(px - c.X) > 1e-3 || Math.Abs(py - c.Y) > 1e-3)
                {
                    continue;
                }

                onSeg.Add((t, c));
            }

            foreach (var item in onSeg.OrderBy(x => x.T))
            {
                result.Add(item.P);
            }
        }

        result.Add(poly[^1]);
        return Deduplicate(result);
    }

    private static SeedElevationLookup BuildElevationLookup(IReadOnlyList<Point3d> seed)
    {
        var tris = TerrainDelaunay.Triangulate(seed);
        var triangles = new List<TerrainTriangle>(tris.Count);
        var edges = new HashSet<(long, long, long, long)>();
        var edgeList = new List<(Point3d A, Point3d B)>();

        foreach (var t in tris)
        {
            var a = seed[t.A];
            var b = seed[t.B];
            var c = seed[t.C];
            triangles.Add(new TerrainTriangle(a, b, c));
            AddUniqueEdge(edges, edgeList, a, b);
            AddUniqueEdge(edges, edgeList, b, c);
            AddUniqueEdge(edges, edgeList, c, a);
        }

        return new SeedElevationLookup(seed, triangles, edgeList);
    }

    private static void AddUniqueEdge(
        HashSet<(long, long, long, long)> seen,
        List<(Point3d A, Point3d B)> edges,
        Point3d a,
        Point3d b)
    {
        var qa = Quantize(a);
        var qb = Quantize(b);
        var key = qa.Item1 < qb.Item1 || (qa.Item1 == qb.Item1 && qa.Item2 <= qb.Item2)
            ? (qa.Item1, qa.Item2, qb.Item1, qb.Item2)
            : (qb.Item1, qb.Item2, qa.Item1, qa.Item2);
        if (!seen.Add(key))
        {
            return;
        }

        edges.Add((a, b));
    }

    private static bool TryIntersectPlanSegments(
        Point2d a,
        Point2d b,
        Point2d c,
        Point2d d,
        out double tAb,
        out double tCd)
    {
        tAb = 0;
        tCd = 0;
        var ax = b.X - a.X;
        var ay = b.Y - a.Y;
        var cx = d.X - c.X;
        var cy = d.Y - c.Y;
        var den = ax * cy - ay * cx;
        if (Math.Abs(den) < 1e-14)
        {
            return false;
        }

        var dx = c.X - a.X;
        var dy = c.Y - a.Y;
        tAb = (dx * cy - dy * cx) / den;
        tCd = (dx * ay - dy * ax) / den;
        // Strict proper intersection (not at endpoints of TIN edge — endpoint already exists).
        return tAb > 1e-6 && tAb < 1.0 - 1e-6 && tCd > 1e-6 && tCd < 1.0 - 1e-6;
    }

    private static void MergePointPreferTerrainZ(List<Point3d> dest, Point3d p)
    {
        for (var i = 0; i < dest.Count; i++)
        {
            if (Math.Abs(dest[i].X - p.X) <= Tol && Math.Abs(dest[i].Y - p.Y) <= Tol)
            {
                // Ako postojeća tačka ima Z≈0 a nova ima teren — zameni.
                if (Math.Abs(dest[i].Z) < 1e-6 && Math.Abs(p.Z) > 1e-6)
                {
                    dest[i] = p;
                }

                return;
            }
        }

        // Ne dodaj čistu Z=0 tačku ako već imamo teren sa kotama.
        if (Math.Abs(p.Z) < 1e-6 && dest.Any(v => Math.Abs(v.Z) > 1e-3))
        {
            return;
        }

        dest.Add(p);
    }

    private sealed class SeedElevationLookup
    {
        private readonly IReadOnlyList<Point3d> _seed;
        private readonly IReadOnlyList<TerrainTriangle> _triangles;

        public IReadOnlyList<(Point3d A, Point3d B)> Edges { get; }

        public SeedElevationLookup(
            IReadOnlyList<Point3d> seed,
            IReadOnlyList<TerrainTriangle> triangles,
            IReadOnlyList<(Point3d A, Point3d B)> edges)
        {
            _seed = seed;
            _triangles = triangles;
            Edges = edges;
        }

        public bool TryGetElevation(double x, double y, out double z)
        {
            foreach (var tri in _triangles)
            {
                if (tri.TryGetElevation(x, y, out z))
                {
                    return true;
                }
            }

            z = 0;
            return false;
        }

        public bool TryNearestAverage(double x, double y, out double z)
        {
            z = 0;
            if (_seed.Count == 0)
            {
                return false;
            }

            var nearest = _seed
                .Select(p => (P: p, D: (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)))
                .OrderBy(t => t.D)
                .Take(3)
                .ToList();
            if (nearest.Count == 0 || nearest[0].D > 1e10)
            {
                return false;
            }

            double wSum = 0, zSum = 0;
            foreach (var n in nearest)
            {
                var w = 1.0 / Math.Max(n.D, 1e-6);
                wSum += w;
                zSum += w * n.P.Z;
            }

            z = zSum / wSum;
            return true;
        }
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
