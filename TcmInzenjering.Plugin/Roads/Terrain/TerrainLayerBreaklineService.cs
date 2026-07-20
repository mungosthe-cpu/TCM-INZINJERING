using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Na lejeru nađe linije, pokupi postojeće tačke terena koje leže na njima,
/// pa forsira TIN ivice između uzastopnih tačaka (swap da se linija ne seče).
/// </summary>
internal static class TerrainLayerBreaklineService
{
    public sealed class Result
    {
        public int Curves { get; init; }
        public int PointsMatched { get; init; }
        public int EdgesAdded { get; init; }
        public string LayerName { get; init; } = string.Empty;
        public IReadOnlyList<TerrainEdgeKey> CandidateEdges { get; init; } =
            Array.Empty<TerrainEdgeKey>();

        /// <summary>
        /// Temena (lomovi) linije između dve uparene tačke terena koja nemaju svoju
        /// tačku — Z je interpolisana po dužini linije. Komanda ih dodaje u teren.
        /// </summary>
        public IReadOnlyList<Point3d> VertexPointsAdded { get; init; } =
            Array.Empty<Point3d>();
    }

    public static Result Apply(
        Transaction tr,
        Database db,
        string layerName,
        IReadOnlyList<Point3d> terrainPoints,
        double xyTolerance,
        IProgress<(int Percent, string Status)>? progress = null)
    {
        layerName = (layerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(layerName))
        {
            throw new ArgumentException("Ime lejera je obavezno.");
        }

        if (xyTolerance <= 0)
        {
            xyTolerance = 0.05;
        }

        progress?.Report((5, $"Tražim linije na lejeru „{layerName}“…"));
        var curves = CollectCurvesOnLayer(tr, db, layerName);
        if (curves.Count == 0)
        {
            return new Result { LayerName = layerName, Curves = 0 };
        }

        progress?.Report((12, $"Pronadjeno {curves.Count} linija — mapiram tacke…"));

        var forced = TerrainDefinitionStore.LoadForcedEdges(tr, db).ToList();
        var edgesAdded = 0;
        var matched = new HashSet<int>();
        var candidates = new List<TerrainEdgeKey>();
        var vertexAdded = new List<Point3d>();

        // Prolaz 1: mapiraj tacke terena na svaku liniju (i linije sa samo jednom
        // tackom ulaze u obzir — mozda im nedostaje tacka na spoju sa susednom).
        var perCurve = new List<List<(Point3d Point, double Param)>>(curves.Count);
        for (var c = 0; c < curves.Count; c++)
        {
            var curve = curves[c];
            var pct = 12 + (int)(30.0 * (c + 1) / curves.Count);
            progress?.Report((pct, $"Linija {c + 1}/{curves.Count} — uparujem tacke…"));

            var onLine = new List<(Point3d Point, double Param)>();
            for (var i = 0; i < terrainPoints.Count; i++)
            {
                var p = terrainPoints[i];
                if (!TryProjectOnCurve(curve, p, xyTolerance, out var param, out _))
                {
                    continue;
                }

                onLine.Add((p, param));
                matched.Add(i);
            }

            onLine = onLine
                .OrderBy(x => x.Param)
                .GroupBy(x => Math.Round(x.Param, 6))
                .Select(g => g.First())
                .ToList();
            perCurve.Add(onLine);
        }

        // Prolaz 2: tacka loma na spoju dve linije — kraj jedne linije se nastavlja
        // na drugu, a nijedna tacka terena ne pokriva taj spoj. Z je interpolacija
        // izmedju najblizih uparenih tacaka na obe linije, po duzini linija.
        progress?.Report((44, "Trazim lomove bez tacke na spojevima linija…"));
        AddJunctionPoints(curves, perCurve, xyTolerance, vertexAdded);

        // Prolaz 3: forsirane ivice izmedju uzastopnih tacaka na svakoj liniji.
        for (var c = 0; c < curves.Count; c++)
        {
            var onLine = perCurve[c];
            if (onLine.Count < 2)
            {
                continue;
            }

            for (var i = 0; i + 1 < onLine.Count; i++)
            {
                var a = onLine[i].Point;
                var b = onLine[i + 1].Point;
                if (XyDist(a, b) <= 1e-9)
                {
                    continue;
                }

                var candidate = TerrainEdgeKey.Create(a, b);
                if (!candidates.Any(e => e.Matches(candidate.A, candidate.B)))
                {
                    candidates.Add(candidate);
                }

                if (forced.Any(e => e.Matches(a, b)))
                {
                    continue;
                }

                forced.Add(candidate);
                edgesAdded++;
            }
        }

        progress?.Report((52, $"Snimam {edgesAdded} fors. ivica…"));
        TerrainDefinitionStore.SaveForcedEdges(tr, db, forced);

        return new Result
        {
            LayerName = layerName,
            Curves = curves.Count,
            PointsMatched = matched.Count,
            EdgesAdded = edgesAdded,
            CandidateEdges = candidates,
            VertexPointsAdded = vertexAdded
        };
    }

    /// <summary>
    /// Tačke loma na spojevima linija: ako se kraj jedne linije nastavlja na kraj
    /// druge (unutar tolerancije), a nijedna tačka terena ne pokriva taj spoj,
    /// dodaje se nova tačka na spoju. Z se interpoliše između najbliže uparene
    /// tačke na jednoj i najbliže uparene tačke na drugoj liniji, po dužini linija.
    /// </summary>
    private static void AddJunctionPoints(
        IReadOnlyList<Curve> curves,
        List<List<(Point3d Point, double Param)>> perCurve,
        double xyTol,
        List<Point3d> added)
    {
        for (var c1 = 0; c1 < curves.Count; c1++)
        {
            if (perCurve[c1].Count == 0)
            {
                continue;
            }

            TryAddJunction(curves, perCurve, c1, atStart: true, xyTol, added);
            TryAddJunction(curves, perCurve, c1, atStart: false, xyTol, added);
        }
    }

    private static void TryAddJunction(
        IReadOnlyList<Curve> curves,
        List<List<(Point3d Point, double Param)>> perCurve,
        int c1,
        bool atStart,
        double xyTol,
        List<Point3d> added)
    {
        var curve = curves[c1];
        Point3d endpoint;
        double endParam;
        double endDist;
        try
        {
            endpoint = atStart ? curve.StartPoint : curve.EndPoint;
            endParam = atStart ? curve.StartParam : curve.EndParam;
            endDist = atStart ? 0.0 : curve.GetDistAtPoint(curve.EndPoint);
        }
        catch
        {
            return;
        }

        // Kraj je vec pokriven tackom (uparenom ili ranije dodatom)?
        if (perCurve[c1].Any(x => XyDist(x.Point, endpoint) <= xyTol))
        {
            return;
        }

        for (var c2 = 0; c2 < curves.Count; c2++)
        {
            if (c2 == c1 || perCurve[c2].Count == 0)
            {
                continue;
            }

            var other = curves[c2];
            Point3d otherEnd;
            double otherParam;
            double otherEndDist;
            try
            {
                if (XyDist(other.StartPoint, endpoint) <= xyTol)
                {
                    otherEnd = other.StartPoint;
                    otherParam = other.StartParam;
                    otherEndDist = 0.0;
                }
                else if (XyDist(other.EndPoint, endpoint) <= xyTol)
                {
                    otherEnd = other.EndPoint;
                    otherParam = other.EndParam;
                    otherEndDist = other.GetDistAtPoint(other.EndPoint);
                }
                else
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            // Spoj je pokriven tackom sa druge strane — nista ne dodajemo.
            if (perCurve[c2].Any(x => XyDist(x.Point, otherEnd) <= xyTol))
            {
                continue;
            }

            if (!TryNearestAlong(curve, perCurve[c1], endDist, out var a, out var d1) ||
                !TryNearestAlong(other, perCurve[c2], otherEndDist, out var b, out var d2))
            {
                continue;
            }

            var total = d1 + d2;
            var z = total <= 1e-9
                ? (a.Z + b.Z) * 0.5
                : a.Z + (b.Z - a.Z) * (d1 / total);
            var junction = new Point3d(endpoint.X, endpoint.Y, z);

            InsertSorted(perCurve[c1], (junction, endParam));
            InsertSorted(perCurve[c2], (junction, otherParam));
            added.Add(junction);
            return;
        }
    }

    /// <summary>Najbliža uparena tačka po dužini krive od zadate stacionaže kraja.</summary>
    private static bool TryNearestAlong(
        Curve curve,
        IReadOnlyList<(Point3d Point, double Param)> onLine,
        double endDist,
        out Point3d nearest,
        out double dist)
    {
        nearest = Point3d.Origin;
        dist = double.MaxValue;
        foreach (var (p, _) in onLine)
        {
            try
            {
                var closest = curve.GetClosestPointTo(p, false);
                var d = Math.Abs(curve.GetDistAtPoint(closest) - endDist);
                if (d < dist)
                {
                    dist = d;
                    nearest = p;
                }
            }
            catch
            {
                // preskoci tacku koju ne mozemo projektovati
            }
        }

        return dist < double.MaxValue && dist > 1e-9;
    }

    private static void InsertSorted(
        List<(Point3d Point, double Param)> onLine,
        (Point3d Point, double Param) item)
    {
        var idx = onLine.FindIndex(x => x.Param > item.Param);
        if (idx < 0)
        {
            onLine.Add(item);
        }
        else
        {
            onLine.Insert(idx, item);
        }
    }

    internal static List<Curve> CollectCurvesOnLayer(Transaction tr, Database db, string layerName)
    {
        var ms = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        var list = new List<Curve>();
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Curve curve || curve.IsErased)
            {
                continue;
            }

            if (!string.Equals(curve.Layer, layerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (curve is not (Line or Polyline or Polyline2d or Polyline3d))
            {
                continue;
            }

            list.Add(curve);
        }

        return list;
    }

    private static bool TryProjectOnCurve(
        Curve curve,
        Point3d point,
        double xyTol,
        out double param,
        out Point3d closest)
    {
        param = 0;
        closest = point;
        try
        {
            closest = curve.GetClosestPointTo(point, false);
            var dx = closest.X - point.X;
            var dy = closest.Y - point.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > xyTol)
            {
                return false;
            }

            param = curve.GetParameterAtPoint(closest);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double XyDist(Point3d a, Point3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
