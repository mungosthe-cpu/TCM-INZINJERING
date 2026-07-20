using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Proračun zapremine iskop/nasip: TIN–TIN (glavni), Grid (kontrola), sekcije.
/// Konvencija: Baza = postojeće, Poređenje = projektovano.
/// dz = Z_projekat − Z_postojeće → iskop gde dz &lt; 0, nasip gde dz &gt; 0.
/// </summary>
internal static class TerrainVolumeEngine
{
    private const double XyTol = 1e-8;
    private const double AreaEps = 1e-12;

    public sealed class Options
    {
        public double GridStep { get; set; } = 5.0;
        public int SectionCount { get; set; } = 12;
        public double SwellFactor { get; set; } = 1.0;
        public double ShrinkFactor { get; set; } = 1.0;
        public IReadOnlyList<Point2d>? InclusionRing { get; set; }
        public IReadOnlyList<Point2d>? SectionAxis { get; set; }
    }

    private sealed class SurfacePack
    {
        public TerrainMesh Mesh { get; init; } = new(Array.Empty<TerrainTriangle>());
        public IReadOnlyList<Point3d> UniquePoints { get; init; } = Array.Empty<Point3d>();
        public IReadOnlyList<(Point2d A, Point2d B)> Edges { get; init; } =
            Array.Empty<(Point2d, Point2d)>();
        public double MinX { get; init; }
        public double MinY { get; init; }
        public double MaxX { get; init; }
        public double MaxY { get; init; }
    }

    public static TerrainVolumeResult Compute(
        string baseName,
        string comparisonName,
        IReadOnlyList<Point3d> basePoints,
        IReadOnlyList<Point3d> comparisonPoints,
        Options? options = null)
    {
        options ??= new Options();
        var gridStep = Math.Max(0.25, options.GridStep);
        var sectionCount = Math.Max(2, Math.Min(200, options.SectionCount));
        var swell = Math.Max(0.01, options.SwellFactor);
        var shrink = Math.Max(0.01, options.ShrinkFactor);

        var baseSurf = BuildSurface(basePoints);
        var cmpSurf = BuildSurface(comparisonPoints);
        if (baseSurf.Mesh.TriangleCount == 0 || cmpSurf.Mesh.TriangleCount == 0)
        {
            return WarnResult(baseName, comparisonName, gridStep, sectionCount, swell, shrink,
                "Jedan od terena nema dovoljno tacaka za TIN (min. 3).");
        }

        var minX = Math.Max(baseSurf.MinX, cmpSurf.MinX);
        var minY = Math.Max(baseSurf.MinY, cmpSurf.MinY);
        var maxX = Math.Min(baseSurf.MaxX, cmpSurf.MaxX);
        var maxY = Math.Min(baseSurf.MaxY, cmpSurf.MaxY);
        if (options.InclusionRing is { Count: >= 3 })
        {
            RingBounds(options.InclusionRing, out var rMinX, out var rMinY, out var rMaxX, out var rMaxY);
            minX = Math.Max(minX, rMinX);
            minY = Math.Max(minY, rMinY);
            maxX = Math.Min(maxX, rMaxX);
            maxY = Math.Min(maxY, rMaxY);
        }

        if (maxX <= minX + 1e-6 || maxY <= minY + 1e-6)
        {
            return WarnResult(baseName, comparisonName, gridStep, sectionCount, swell, shrink,
                "Nema preklapanja (overlap) izmedju dva terena.");
        }

        var inclusion = options.InclusionRing;
        var tin = ComputeTinComposite(baseSurf, cmpSurf, minX, minY, maxX, maxY, inclusion);
        var (grid, disagreement) = ComputeGridAndDisagreement(
            baseSurf.Mesh, cmpSurf.Mesh, tin.CompositeTriangles,
            minX, minY, maxX, maxY, gridStep, inclusion);
        var sections = ComputeSections(
            baseSurf.Mesh, cmpSurf.Mesh, minX, minY, maxX, maxY,
            sectionCount, inclusion, options.SectionAxis);

        var conf = EvaluateConfidence(tin.Method, grid, sections, gridStep, minX, minY, maxX, maxY);
        var warning = conf.Warning;
        if (gridStep > 0.15 * Math.Max(maxX - minX, maxY - minY))
        {
            warning = (warning is null ? string.Empty : warning + " ") +
                      "Grid korak je veliki u odnosu na AOI — povecajte rezoluciju za pouzdaniju kontrolu.";
        }

        return new TerrainVolumeResult
        {
            BaseName = baseName,
            ComparisonName = comparisonName,
            GridStep = gridStep,
            SectionCount = sectionCount,
            SwellFactor = swell,
            ShrinkFactor = shrink,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            Tin = tin.Method,
            Grid = grid,
            Sections = sections,
            DisagreementCells = disagreement,
            MeanRelativeErrorPercent = conf.MeanRel,
            MaxRelativeErrorPercent = conf.MaxRel,
            ConfidenceLevel = conf.Level,
            ConfidenceNote = conf.Note,
            Warning = string.IsNullOrWhiteSpace(warning) ? null : warning.Trim(),
            ComputedAt = DateTime.Now
        };
    }

    private static TerrainVolumeResult WarnResult(
        string baseName, string comparisonName, double gridStep, int sectionCount,
        double swell, double shrink, string warning) =>
        new()
        {
            BaseName = baseName,
            ComparisonName = comparisonName,
            GridStep = gridStep,
            SectionCount = sectionCount,
            SwellFactor = swell,
            ShrinkFactor = shrink,
            Warning = warning
        };

    private sealed class TinPack
    {
        public TerrainVolumeMethodResult Method { get; init; } = new();
        public IReadOnlyList<(Point3d A, Point3d B, Point3d C, double SignedVol)> CompositeTriangles { get; init; } =
            Array.Empty<(Point3d, Point3d, Point3d, double)>();
    }

    private static TinPack ComputeTinComposite(
        SurfacePack baseSurf,
        SurfacePack cmpSurf,
        double minX,
        double minY,
        double maxX,
        double maxY,
        IReadOnlyList<Point2d>? inclusion)
    {
        var xy = new List<Point3d>();
        void AddXy(double x, double y)
        {
            if (x < minX - 1e-6 || x > maxX + 1e-6 || y < minY - 1e-6 || y > maxY + 1e-6)
            {
                return;
            }

            if (inclusion is { Count: >= 3 } && !PointInRing(x, y, inclusion))
            {
                return;
            }

            if (!baseSurf.Mesh.TryGetElevation(x, y, out var zb) ||
                !cmpSurf.Mesh.TryGetElevation(x, y, out var zc))
            {
                return;
            }

            // Dedup rough
            for (var i = 0; i < xy.Count; i++)
            {
                if (Math.Abs(xy[i].X - x) <= XyTol && Math.Abs(xy[i].Y - y) <= XyTol)
                {
                    return;
                }
            }

            xy.Add(new Point3d(x, y, zc - zb));
        }

        foreach (var p in baseSurf.UniquePoints)
        {
            AddXy(p.X, p.Y);
        }

        foreach (var p in cmpSurf.UniquePoints)
        {
            AddXy(p.X, p.Y);
        }

        foreach (var e in baseSurf.Edges)
        {
            AddXy((e.A.X + e.B.X) * 0.5, (e.A.Y + e.B.Y) * 0.5);
        }

        foreach (var e in cmpSurf.Edges)
        {
            AddXy((e.A.X + e.B.X) * 0.5, (e.A.Y + e.B.Y) * 0.5);
        }

        AddXy(minX, minY);
        AddXy(maxX, minY);
        AddXy(maxX, maxY);
        AddXy(minX, maxY);
        AddXy((minX + maxX) * 0.5, minY);
        AddXy((minX + maxX) * 0.5, maxY);
        AddXy(minX, (minY + maxY) * 0.5);
        AddXy(maxX, (minY + maxY) * 0.5);

        var tris = TerrainDelaunay.Triangulate(xy);
        double cutV = 0, fillV = 0, cutA = 0, fillA = 0;
        var composite = new List<(Point3d A, Point3d B, Point3d C, double SignedVol)>(tris.Count);

        foreach (var t in tris)
        {
            var a = xy[t.A];
            var b = xy[t.B];
            var c = xy[t.C];
            var area = TriangleAreaXy(a, b, c);
            if (area < AreaEps)
            {
                continue;
            }

            var cx = (a.X + b.X + c.X) / 3.0;
            var cy = (a.Y + b.Y + c.Y) / 3.0;
            if (cx < minX || cx > maxX || cy < minY || cy > maxY)
            {
                continue;
            }

            if (inclusion is { Count: >= 3 } && !PointInRing(cx, cy, inclusion))
            {
                continue;
            }

            if (!TryDz(baseSurf.Mesh, cmpSurf.Mesh, a.X, a.Y, out var dza) ||
                !TryDz(baseSurf.Mesh, cmpSurf.Mesh, b.X, b.Y, out var dzb) ||
                !TryDz(baseSurf.Mesh, cmpSurf.Mesh, c.X, c.Y, out var dzc))
            {
                continue;
            }

            var avgDz = (dza + dzb + dzc) / 3.0;
            var signed = avgDz * area;
            composite.Add((
                new Point3d(a.X, a.Y, dza),
                new Point3d(b.X, b.Y, dzb),
                new Point3d(c.X, c.Y, dzc),
                signed));
            AccumulateSigned(signed, area, avgDz, ref cutV, ref fillV, ref cutA, ref fillA);
        }

        return new TinPack
        {
            Method = new TerrainVolumeMethodResult
            {
                MethodName = "TIN–TIN",
                CutVolume = cutV,
                FillVolume = fillV,
                CutArea = cutA,
                FillArea = fillA
            },
            CompositeTriangles = composite
        };
    }

    private static (TerrainVolumeMethodResult Grid, List<TerrainVolumeDisagreementCell> Cells)
        ComputeGridAndDisagreement(
            TerrainMesh baseMesh,
            TerrainMesh cmpMesh,
            IReadOnlyList<(Point3d A, Point3d B, Point3d C, double SignedVol)> composite,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double step,
            IReadOnlyList<Point2d>? inclusion)
    {
        double cutV = 0, fillV = 0, cutA = 0, fillA = 0;
        var cells = new List<TerrainVolumeDisagreementCell>();
        var tinByCell = new Dictionary<(int, int), double>();

        foreach (var tri in composite)
        {
            var cx = (tri.A.X + tri.B.X + tri.C.X) / 3.0;
            var cy = (tri.A.Y + tri.B.Y + tri.C.Y) / 3.0;
            var ix = (int)Math.Floor((cx - minX) / step);
            var iy = (int)Math.Floor((cy - minY) / step);
            tinByCell.TryGetValue((ix, iy), out var sum);
            tinByCell[(ix, iy)] = sum + tri.SignedVol;
        }

        var nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / step));
        var ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / step));
        for (var ix = 0; ix < nx; ix++)
        {
            for (var iy = 0; iy < ny; iy++)
            {
                var x0 = minX + ix * step;
                var y0 = minY + iy * step;
                var x1 = Math.Min(maxX, x0 + step);
                var y1 = Math.Min(maxY, y0 + step);
                if (x1 - x0 < 1e-9 || y1 - y0 < 1e-9)
                {
                    continue;
                }

                var cellCx = (x0 + x1) * 0.5;
                var cellCy = (y0 + y1) * 0.5;
                if (inclusion is { Count: >= 3 } && !PointInRing(cellCx, cellCy, inclusion))
                {
                    continue;
                }

                if (!TryDz(baseMesh, cmpMesh, x0, y0, out var d00) ||
                    !TryDz(baseMesh, cmpMesh, x1, y0, out var d10) ||
                    !TryDz(baseMesh, cmpMesh, x1, y1, out var d11) ||
                    !TryDz(baseMesh, cmpMesh, x0, y1, out var d01))
                {
                    continue;
                }

                var avgDz = (d00 + d10 + d11 + d01) * 0.25;
                var area = (x1 - x0) * (y1 - y0);
                var signed = avgDz * area;
                AccumulateSigned(signed, area, avgDz, ref cutV, ref fillV, ref cutA, ref fillA);

                tinByCell.TryGetValue((ix, iy), out var tinSigned);
                var absDiff = Math.Abs(tinSigned - signed);
                if (absDiff > 1e-6 || Math.Abs(signed) > 1e-6 || Math.Abs(tinSigned) > 1e-6)
                {
                    cells.Add(new TerrainVolumeDisagreementCell(
                        x0, y0, x1, y1, tinSigned, signed, absDiff));
                }
            }
        }

        return (new TerrainVolumeMethodResult
        {
            MethodName = "Grid",
            CutVolume = cutV,
            FillVolume = fillV,
            CutArea = cutA,
            FillArea = fillA
        }, cells);
    }

    private static TerrainVolumeMethodResult ComputeSections(
        TerrainMesh baseMesh,
        TerrainMesh cmpMesh,
        double minX,
        double minY,
        double maxX,
        double maxY,
        int sectionCount,
        IReadOnlyList<Point2d>? inclusion,
        IReadOnlyList<Point2d>? axis)
    {
        var width = maxX - minX;
        var height = maxY - minY;
        var alongX = width >= height;
        var stations = new List<(double S, double CutArea, double FillArea)>();

        if (axis is { Count: >= 2 })
        {
            var totalLen = PolyLength(axis);
            if (totalLen < 1e-6)
            {
                return EmptyMethod("Sekcije");
            }

            for (var i = 0; i < sectionCount; i++)
            {
                var t = sectionCount == 1 ? 0.5 : i / (double)(sectionCount - 1);
                var s = t * totalLen;
                var (px, py, tx, ty) = PointAndTangentAt(axis, s);
                var nx = -ty;
                var ny = tx;
                var lenN = Math.Sqrt(nx * nx + ny * ny);
                if (lenN < 1e-12)
                {
                    continue;
                }

                nx /= lenN;
                ny /= lenN;
                var half = 0.5 * Math.Sqrt(width * width + height * height);
                SampleSectionAreas(
                    baseMesh, cmpMesh, px, py, nx, ny, half, inclusion,
                    out var cutA, out var fillA);
                stations.Add((s, cutA, fillA));
            }
        }
        else
        {
            for (var i = 0; i < sectionCount; i++)
            {
                var t = sectionCount == 1 ? 0.5 : i / (double)(sectionCount - 1);
                double cutA, fillA, s;
                if (alongX)
                {
                    var x = minX + t * width;
                    s = x - minX;
                    SampleSectionAreas(
                        baseMesh, cmpMesh, x, (minY + maxY) * 0.5, 0, 1, height * 0.5 + 1,
                        inclusion, out cutA, out fillA);
                }
                else
                {
                    var y = minY + t * height;
                    s = y - minY;
                    SampleSectionAreas(
                        baseMesh, cmpMesh, (minX + maxX) * 0.5, y, 1, 0, width * 0.5 + 1,
                        inclusion, out cutA, out fillA);
                }

                stations.Add((s, cutA, fillA));
            }
        }

        if (stations.Count < 2)
        {
            return EmptyMethod("Sekcije");
        }

        stations.Sort((a, b) => a.S.CompareTo(b.S));
        double cutV = 0, fillV = 0, cutASum = 0, fillASum = 0;
        for (var i = 0; i < stations.Count - 1; i++)
        {
            var a = stations[i];
            var b = stations[i + 1];
            var ds = Math.Abs(b.S - a.S);
            if (ds < 1e-9)
            {
                continue;
            }

            cutV += (a.CutArea + b.CutArea) * 0.5 * ds;
            fillV += (a.FillArea + b.FillArea) * 0.5 * ds;
        }

        foreach (var st in stations)
        {
            cutASum += st.CutArea;
            fillASum += st.FillArea;
        }

        var n = stations.Count;
        return new TerrainVolumeMethodResult
        {
            MethodName = "Sekcije",
            CutVolume = cutV,
            FillVolume = fillV,
            CutArea = cutASum / n,
            FillArea = fillASum / n
        };
    }

    private static void SampleSectionAreas(
        TerrainMesh baseMesh,
        TerrainMesh cmpMesh,
        double ox,
        double oy,
        double nx,
        double ny,
        double halfWidth,
        IReadOnlyList<Point2d>? inclusion,
        out double cutArea,
        out double fillArea)
    {
        cutArea = 0;
        fillArea = 0;
        const int samples = 80;
        var prevOff = -halfWidth;
        double? prevDz = null;
        for (var i = 0; i <= samples; i++)
        {
            var off = -halfWidth + (2 * halfWidth) * (i / (double)samples);
            var x = ox + nx * off;
            var y = oy + ny * off;
            if (inclusion is { Count: >= 3 } && !PointInRing(x, y, inclusion))
            {
                prevDz = null;
                prevOff = off;
                continue;
            }

            if (!TryDz(baseMesh, cmpMesh, x, y, out var dz))
            {
                prevDz = null;
                prevOff = off;
                continue;
            }

            if (prevDz is double pdz)
            {
                AccumulateSectionTrapezoid(pdz, dz, off - prevOff, ref cutArea, ref fillArea);
            }

            prevDz = dz;
            prevOff = off;
        }
    }

    private static void AccumulateSectionTrapezoid(
        double dz0, double dz1, double width, ref double cutArea, ref double fillArea)
    {
        if (Math.Abs(width) < 1e-15)
        {
            return;
        }

        var w = Math.Abs(width);
        if (dz0 >= 0 && dz1 >= 0)
        {
            fillArea += (dz0 + dz1) * 0.5 * w;
            return;
        }

        if (dz0 <= 0 && dz1 <= 0)
        {
            cutArea += (-dz0 - dz1) * 0.5 * w;
            return;
        }

        var t = Math.Abs(dz0) / (Math.Abs(dz0) + Math.Abs(dz1) + 1e-18);
        var w0 = w * t;
        var w1 = w - w0;
        if (dz0 > 0)
        {
            fillArea += dz0 * 0.5 * w0;
            cutArea += (-dz1) * 0.5 * w1;
        }
        else
        {
            cutArea += (-dz0) * 0.5 * w0;
            fillArea += dz1 * 0.5 * w1;
        }
    }

    private static (double MeanRel, double MaxRel, string Level, string Note, string? Warning)
        EvaluateConfidence(
            TerrainVolumeMethodResult tin,
            TerrainVolumeMethodResult grid,
            TerrainVolumeMethodResult sections,
            double gridStep,
            double minX,
            double minY,
            double maxX,
            double maxY)
    {
        var errGrid = RelError(tin, grid) * 100.0;
        var errSec = RelError(tin, sections) * 100.0;
        var mean = (errGrid + errSec) * 0.5;
        var max = Math.Max(errGrid, errSec);

        string level;
        string note;
        if (max <= 2.0)
        {
            level = "Visoka";
            note = $"Metode se slazu (max odstupanje {max:0.00}%). TIN–TIN je pouzdan.";
        }
        else if (max <= 5.0)
        {
            level = "Srednja";
            note = $"Umereno odstupanje (max {max:0.00}%). Proverite mapu neslaganja i smanjite grid korak.";
        }
        else
        {
            level = "Niska";
            note = $"Veliko odstupanje (max {max:0.00}%). Grid/sekcije verovatno promašuju uske kanale / breakline.";
        }

        string? warning = null;
        var diag = Math.Max(maxX - minX, maxY - minY);
        if (gridStep > diag / 20.0 && max > 2.0)
        {
            warning = "Grid korak je grub u odnosu na teren — smanjite korak (npr. 1–2 m).";
        }

        return (mean, max, level, note, warning);
    }

    private static double RelError(TerrainVolumeMethodResult a, TerrainVolumeMethodResult b)
    {
        var refCut = Math.Max(Math.Abs(a.CutVolume), 1e-6);
        var refFill = Math.Max(Math.Abs(a.FillVolume), 1e-6);
        var eCut = Math.Abs(a.CutVolume - b.CutVolume) / refCut;
        var eFill = Math.Abs(a.FillVolume - b.FillVolume) / refFill;
        var refNet = Math.Max(Math.Abs(a.NetVolume), 1e-6);
        var eNet = Math.Abs(a.NetVolume - b.NetVolume) / refNet;
        return Math.Max(eCut, Math.Max(eFill, eNet));
    }

    private static SurfacePack BuildSurface(IReadOnlyList<Point3d> points)
    {
        var unique = DeduplicateXy(points);
        if (unique.Count < 3)
        {
            return new SurfacePack();
        }

        var trisIdx = TerrainDelaunay.Triangulate(unique);
        var triangles = new List<TerrainTriangle>(trisIdx.Count);
        var edgeSet = new HashSet<(long, long, long, long)>();
        var edges = new List<(Point2d A, Point2d B)>();
        foreach (var t in trisIdx)
        {
            triangles.Add(new TerrainTriangle(unique[t.A], unique[t.B], unique[t.C]));
            AddEdge(edgeSet, edges, unique[t.A], unique[t.B]);
            AddEdge(edgeSet, edges, unique[t.B], unique[t.C]);
            AddEdge(edgeSet, edges, unique[t.C], unique[t.A]);
        }

        var minX = unique.Min(p => p.X);
        var minY = unique.Min(p => p.Y);
        var maxX = unique.Max(p => p.X);
        var maxY = unique.Max(p => p.Y);
        return new SurfacePack
        {
            Mesh = new TerrainMesh(triangles),
            UniquePoints = unique,
            Edges = edges,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY
        };
    }

    private static void AddEdge(
        HashSet<(long, long, long, long)> set,
        List<(Point2d A, Point2d B)> edges,
        Point3d a,
        Point3d b)
    {
        var ax = (long)Math.Round(a.X * 1e4);
        var ay = (long)Math.Round(a.Y * 1e4);
        var bx = (long)Math.Round(b.X * 1e4);
        var by = (long)Math.Round(b.Y * 1e4);
        var key = ax < bx || (ax == bx && ay <= by) ? (ax, ay, bx, by) : (bx, by, ax, ay);
        if (!set.Add(key))
        {
            return;
        }

        edges.Add((new Point2d(a.X, a.Y), new Point2d(b.X, b.Y)));
    }

    /// <summary>Sintetički self-check: plato 10×10×1 m ≈ 100 m³ nasip.</summary>
    public static string RunSelfCheck()
    {
        var basePts = new List<Point3d>
        {
            new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0), new(5, 5, 0)
        };
        var cmpPts = new List<Point3d>
        {
            new(0, 0, 1), new(10, 0, 1), new(10, 10, 1), new(0, 10, 1), new(5, 5, 1)
        };

        var r = Compute("Baza", "Plato", basePts, cmpPts, new Options
        {
            GridStep = 1.0,
            SectionCount = 10
        });
        var same = Compute("A", "A", basePts, basePts, new Options { GridStep = 2, SectionCount = 5 });
        var tinErr = Math.Abs(r.Tin.FillVolume - 100.0);
        var zeroOk = Math.Abs(same.Tin.CutVolume) < 0.5 && Math.Abs(same.Tin.FillVolume) < 0.5;
        if (tinErr < 5.0 && zeroOk && r.Tin.CutVolume < 1.0)
        {
            return $"OK: plato fill={r.Tin.FillVolume:0.00} m³ (~100), isti-isti≈0, poverenje={r.ConfidenceLevel}.";
        }

        return $"FAIL: fill={r.Tin.FillVolume:0.00} cut={r.Tin.CutVolume:0.00} " +
               $"sameCut={same.Tin.CutVolume:0.00} sameFill={same.Tin.FillVolume:0.00}";
    }

    private static List<Point3d> DeduplicateXy(IReadOnlyList<Point3d> points)
    {
        var unique = new List<Point3d>(points.Count);
        foreach (var p in points)
        {
            var dup = false;
            for (var i = 0; i < unique.Count; i++)
            {
                if (Math.Abs(p.X - unique[i].X) <= XyTol && Math.Abs(p.Y - unique[i].Y) <= XyTol)
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

    private static bool TryDz(TerrainMesh baseMesh, TerrainMesh cmpMesh, double x, double y, out double dz)
    {
        dz = 0;
        if (!baseMesh.TryGetElevation(x, y, out var zb) ||
            !cmpMesh.TryGetElevation(x, y, out var zc))
        {
            return false;
        }

        dz = zc - zb;
        return true;
    }

    private static void AccumulateSigned(
        double signed, double area, double avgDz,
        ref double cutV, ref double fillV, ref double cutA, ref double fillA)
    {
        if (signed < 0)
        {
            cutV += -signed;
            if (avgDz < 0)
            {
                cutA += area;
            }
        }
        else if (signed > 0)
        {
            fillV += signed;
            if (avgDz > 0)
            {
                fillA += area;
            }
        }
    }

    private static TerrainVolumeMethodResult EmptyMethod(string name) =>
        new() { MethodName = name };

    private static double TriangleAreaXy(Point3d a, Point3d b, Point3d c) =>
        0.5 * Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));

    private static void RingBounds(
        IReadOnlyList<Point2d> ring,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = ring[0].X;
        minY = ring[0].Y;
        maxX = ring[0].X;
        maxY = ring[0].Y;
        for (var i = 1; i < ring.Count; i++)
        {
            minX = Math.Min(minX, ring[i].X);
            minY = Math.Min(minY, ring[i].Y);
            maxX = Math.Max(maxX, ring[i].X);
            maxY = Math.Max(maxY, ring[i].Y);
        }
    }

    internal static bool PointInRing(double x, double y, IReadOnlyList<Point2d> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var xi = ring[i].X;
            var yi = ring[i].Y;
            var xj = ring[j].X;
            var yj = ring[j].Y;
            var intersect = ((yi > y) != (yj > y)) &&
                            (x < (xj - xi) * (y - yi) / ((yj - yi) + 1e-18) + xi);
            if (intersect)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double PolyLength(IReadOnlyList<Point2d> pts)
    {
        double len = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            var dx = pts[i].X - pts[i - 1].X;
            var dy = pts[i].Y - pts[i - 1].Y;
            len += Math.Sqrt(dx * dx + dy * dy);
        }

        return len;
    }

    private static (double X, double Y, double Tx, double Ty) PointAndTangentAt(
        IReadOnlyList<Point2d> pts, double distance)
    {
        var remain = distance;
        for (var i = 1; i < pts.Count; i++)
        {
            var dx = pts[i].X - pts[i - 1].X;
            var dy = pts[i].Y - pts[i - 1].Y;
            var seg = Math.Sqrt(dx * dx + dy * dy);
            if (seg < 1e-12)
            {
                continue;
            }

            if (remain <= seg || i == pts.Count - 1)
            {
                var t = Math.Max(0, Math.Min(1, remain / seg));
                return (pts[i - 1].X + dx * t, pts[i - 1].Y + dy * t, dx / seg, dy / seg);
            }

            remain -= seg;
        }

        var last = pts[^1];
        var prev = pts[^2];
        var ldx = last.X - prev.X;
        var ldy = last.Y - prev.Y;
        var llen = Math.Sqrt(ldx * ldx + ldy * ldy);
        return llen < 1e-12
            ? (last.X, last.Y, 1, 0)
            : (last.X, last.Y, ldx / llen, ldy / llen);
    }
}
