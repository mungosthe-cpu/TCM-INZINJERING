using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Primena kružnog luka ili spiral–arc–spiral (L1/R/L2) na TS čvoru.
/// </summary>
internal static class CornerCurveApplicator
{
    public static bool Apply(
        Transaction tr,
        Database db,
        string axisName,
        int nodeNumber,
        double radius,
        double l1,
        double l2,
        double startStation,
        out string? error)
    {
        error = null;
        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
        if (axis is null || axis.Elements.Count < 3)
        {
            error = "Osovina nije pronadjena ili nema dovoljno elemenata.";
            return false;
        }

        if (!TryReplaceCorner(
                axis.Elements.ToList(),
                nodeNumber,
                radius,
                l1,
                l2,
                startStation,
                out var rebuilt,
                out error))
        {
            return false;
        }

        var metadata = RoadAxisStore.Load(tr, db, axisName);
        var color = metadata?.AxisColorIndex ?? DrawingColorDefaults.Axis;
        ObjectId sourceId = ObjectId.Null;
        if (metadata?.HasSourcePolyline == true)
        {
            AxisPolylineResolver.TryResolve(db, metadata.SourcePolylineHandle, out sourceId);
        }

        var newAxis = new RoadAxis
        {
            Name = axisName,
            StartStation = startStation,
            Elements = rebuilt
        };

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        StationLabelService.DeleteAxisEntities(tr, db, axisName);
        RoadDrawing.DrawAxisCore(tr, modelSpace, newAxis, sourceId, color);

        CornerCurveStore.Set(tr, db, axisName, nodeNumber, radius, l1, l2);
        return true;
    }

    /// <summary>
    /// Posle rebuild-a iz polilinije (samo kružni lukovi): vrati sačuvane L1/R/L2 prelaznice.
    /// </summary>
    public static RoadAxis ApplySavedCurves(Transaction tr, Database db, RoadAxis axis)
    {
        var curves = CornerCurveStore.Load(tr, db, axis.Name);
        if (curves.Count == 0 || axis.Elements.Count < 3)
        {
            return axis;
        }

        var elements = axis.Elements.ToList();
        var startStation = axis.StartStation;
        foreach (var pair in curves.OrderBy(p => p.Key))
        {
            if (pair.Value.L1 <= 1e-6 && pair.Value.L2 <= 1e-6)
            {
                // Samo R — PolylineToTangentConverter / CornerRadiusStore već pokrivaju.
                continue;
            }

            if (!TryReplaceCorner(
                    elements,
                    pair.Key,
                    pair.Value.Radius,
                    pair.Value.L1,
                    pair.Value.L2,
                    startStation,
                    out var rebuilt,
                    out _))
            {
                continue;
            }

            elements = rebuilt;
        }

        return new RoadAxis
        {
            Name = axis.Name,
            StartStation = startStation,
            Elements = elements
        };
    }

    private static bool TryReplaceCorner(
        List<AlignmentElement> elements,
        int nodeNumber,
        double radius,
        double l1,
        double l2,
        double startStation,
        out List<AlignmentElement> rebuilt,
        out string? error)
    {
        rebuilt = elements;
        error = null;
        l1 = Math.Max(0, l1);
        l2 = Math.Max(0, l2);

        if (!TryFindCorner(elements, nodeNumber, out var prevIdx, out _, out var curveEnd))
        {
            error = $"Nije pronadjen TS{nodeNumber} na osovini.";
            return false;
        }

        var prev = elements[prevIdx];
        var next = elements[curveEnd];
        if (prev.Type != AlignmentElementType.Tangent || next.Type != AlignmentElementType.Tangent)
        {
            error = "Susedi cvora moraju biti tangente.";
            return false;
        }

        var p0 = TangentArcGeometry.To2d(prev.Start);
        var p1 = TangentArcGeometry.To2d(prev.End);
        var p2 = TangentArcGeometry.To2d(next.Start);
        var p3 = TangentArcGeometry.To2d(next.End);
        if (!TangentArcGeometry.TryIntersectLines(p0, p1, p2, p3, out var pi))
        {
            error = "Tangente se ne seku — nije moguce odrediti TS.";
            return false;
        }

        var inDir = new Vector2d(p1.X - p0.X, p1.Y - p0.Y);
        var outDir = new Vector2d(p3.X - p2.X, p3.Y - p2.Y);
        if (inDir.Length < 1e-9 || outDir.Length < 1e-9)
        {
            error = "Nevalidan smer tangenti.";
            return false;
        }

        inDir = inDir.GetNormal();
        outDir = outDir.GetNormal();
        var maxIn = Math.Abs(new Vector2d(p0.X - pi.X, p0.Y - pi.Y).DotProduct(inDir));
        var maxOut = Math.Abs(new Vector2d(p3.X - pi.X, p3.Y - pi.Y).DotProduct(outDir));

        List<AlignmentElement> middle;
        if (l1 <= 1e-6 && l2 <= 1e-6)
        {
            if (!TangentArcGeometry.TryBuildCornerArc(
                    pi,
                    inDir,
                    outDir,
                    radius,
                    out var arcStart,
                    out var arcEnd,
                    out var appliedR,
                    out var clockwise,
                    maxIn * 0.98,
                    maxOut * 0.98))
            {
                error =
                    $"Kruzni luk R={radius:0.###} nije moguc (prekratke tangente ili prevelik R).";
                return false;
            }

            middle =
            [
                TangentArcGeometry.CreateArcElement(arcStart, arcEnd, appliedR, clockwise)
            ];
            prev = CreateTangent(p0, arcStart);
            next = CreateTangent(arcEnd, p3);
        }
        else
        {
            if (!ClothoidGeometry.TryBuildCornerSas(
                    pi,
                    inDir,
                    outDir,
                    radius,
                    l1,
                    l2,
                    maxIn * 0.98,
                    maxOut * 0.98,
                    out var sas,
                    out error) ||
                sas is null)
            {
                return false;
            }

            middle = BuildSasElements(sas);
            prev = CreateTangent(p0, sas.Ts);
            next = CreateTangent(sas.St, p3);
        }

        rebuilt = new List<AlignmentElement>();
        for (var i = 0; i < prevIdx; i++)
        {
            rebuilt.Add(elements[i]);
        }

        rebuilt.Add(prev);
        rebuilt.AddRange(middle);
        rebuilt.Add(next);
        for (var i = curveEnd + 1; i < elements.Count; i++)
        {
            rebuilt.Add(elements[i]);
        }

        ArcOrientation.OrientArcsToChainage(rebuilt);
        AssignStations(rebuilt, startStation);
        return true;
    }

    private static List<AlignmentElement> BuildSasElements(ClothoidGeometry.CornerSasResult sas)
    {
        var list = new List<AlignmentElement>();
        if (sas.L1 > 1e-6)
        {
            list.Add(CreateSpiral(sas.EntranceSamples, sas.Radius, sas.L1, sas.Clockwise));
        }

        var arcLen = sas.Radius * sas.CircularSweepRadians;
        list.Add(new AlignmentElement
        {
            Type = AlignmentElementType.Arc,
            Start = TangentArcGeometry.To3d(sas.Sc),
            End = TangentArcGeometry.To3d(sas.Cs),
            Length = Math.Max(arcLen, 1e-6),
            Radius = sas.Radius,
            Center = TangentArcGeometry.To3d(sas.Center),
            Clockwise = sas.Clockwise
        });

        if (sas.L2 > 1e-6)
        {
            list.Add(CreateSpiral(sas.ExitSamples, sas.Radius, sas.L2, sas.Clockwise));
        }

        return list;
    }

    private static AlignmentElement CreateSpiral(
        IReadOnlyList<Point2d> samples,
        double endRadius,
        double length,
        bool clockwise)
    {
        var pts = samples.Select(TangentArcGeometry.To3d).ToList();
        if (pts.Count < 2)
        {
            pts =
            [
                Point3d.Origin,
                Point3d.Origin
            ];
        }

        return new AlignmentElement
        {
            Type = AlignmentElementType.Spiral,
            Start = pts[0],
            End = pts[^1],
            Length = length,
            Radius = endRadius,
            Center = Point3d.Origin,
            Clockwise = clockwise,
            SpiralPoints = pts,
            SpiralA = Math.Sqrt(Math.Max(1e-12, endRadius * length))
        };
    }

    private static AlignmentElement CreateTangent(Point2d a, Point2d b)
    {
        var a3 = TangentArcGeometry.To3d(a);
        var b3 = TangentArcGeometry.To3d(b);
        return new AlignmentElement
        {
            Type = AlignmentElementType.Tangent,
            Start = a3,
            End = b3,
            Length = a3.DistanceTo(b3),
            Radius = 0,
            Center = Point3d.Origin,
            Clockwise = false
        };
    }

    /// <summary>
    /// nodeNumber 1-based među krivinama; vraća indeks ulazne tangente i indeks izlazne tangente.
    /// curveStart..curveEnd-1 su S/A/S elementi.
    /// </summary>
    private static bool TryFindCorner(
        IReadOnlyList<AlignmentElement> elements,
        int nodeNumber,
        out int prevTangentIndex,
        out int curveStart,
        out int nextTangentIndex)
    {
        prevTangentIndex = -1;
        curveStart = -1;
        nextTangentIndex = -1;
        var n = 0;
        for (var i = 1; i < elements.Count - 1; i++)
        {
            if (elements[i].Type != AlignmentElementType.Arc)
            {
                continue;
            }

            // Proširi nalevo/nadesno na spirale.
            var start = i;
            while (start > 0 && elements[start - 1].Type == AlignmentElementType.Spiral)
            {
                start--;
            }

            var end = i;
            while (end + 1 < elements.Count && elements[end + 1].Type == AlignmentElementType.Spiral)
            {
                end++;
            }

            if (start == 0 || end + 1 >= elements.Count)
            {
                continue;
            }

            if (elements[start - 1].Type != AlignmentElementType.Tangent ||
                elements[end + 1].Type != AlignmentElementType.Tangent)
            {
                continue;
            }

            n++;
            if (n == nodeNumber)
            {
                prevTangentIndex = start - 1;
                curveStart = start;
                nextTangentIndex = end + 1;
                return true;
            }
        }

        return false;
    }

    private static void AssignStations(List<AlignmentElement> elements, double startStation)
    {
        var station = startStation;
        foreach (var element in elements)
        {
            element.StartStation = station;
            station += element.Length;
            element.EndStation = station;
        }
    }
}
