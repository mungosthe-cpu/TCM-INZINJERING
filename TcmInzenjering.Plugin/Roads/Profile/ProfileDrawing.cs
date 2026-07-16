using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>CGSA-style podužni: grafik terena + banderola (OZNAKE / STACIONAŽE / KOTE TERENA).</summary>
internal static class ProfileDrawing
{
    public const string LayerFrame = "TCM_POD_OKVIR";
    /// <summary>Horizontalne linije mreže (kote).</summary>
    public const string LayerGrid = "TCM_POD_MREZA";
    /// <summary>Poprečne (vertikalne) linije mreže u grafiku.</summary>
    public const string LayerCrossGrid = "TCM_POP_MREZA";
    public const string LayerVertical = "TCM_POD_VERT";
    public const string LayerTerrain = "TCM_POD_TEREN";
    public const string LayerText = "TCM_POD_TEKST";

    public static void EnsureLayers(Transaction tr, Database db)
    {
        EnsureLayer(tr, db, LayerFrame, 7);
        EnsureLayer(tr, db, LayerGrid, 8);
        EnsureLayer(tr, db, LayerCrossGrid, 8);
        EnsureLayer(tr, db, LayerVertical, 5);
        EnsureLayer(tr, db, LayerTerrain, 3);
        EnsureLayer(tr, db, LayerText, 7);
    }

    /// <summary>Crta tabelu + mrežu + teren (kompletan Profile View).</summary>
    public static void DrawFullProfile(
        Transaction tr,
        BlockTableRecord modelSpace,
        ProfileViewData view,
        IReadOnlyList<(double Station, double Elevation)> terrainSamples)
    {
        EnsureLayers(tr, modelSpace.Database);
        RoadDrawing.EnsureRegApp(tr, modelSpace.Database);

        var tableType = view.ResolveTableType();
        var bands = view.ResolveBands();
        var labelW = view.LabelColumnWidth;
        var width = view.ProfileWidth;
        var xLeft = view.Origin.X;
        var xData = view.DataOriginX;
        var xRight = xData + width;
        var y0 = view.Origin.Y;
        var yGraphBot = view.GraphBottomY;
        var yGraphTop = view.GraphTopY;

        // Spoljni okvir (kolona naziva + podaci + grafik)
        AppendRect(tr, modelSpace, view, xLeft, y0, xRight, yGraphTop, LayerFrame);
        AppendVLine(tr, modelSpace, view, xData, y0, yGraphTop, LayerFrame);
        AppendHLine(tr, modelSpace, view, xLeft, xRight, yGraphBot, LayerFrame);

        var y = y0;
        for (var i = bands.Count - 1; i >= 0; i--)
        {
            y += bands[i].Height;
            if (i > 0)
            {
                AppendHLine(tr, modelSpace, view, xLeft, xRight, y, LayerFrame);
            }
        }

        var labelCenterX = xLeft + labelW * 0.5;
        var titleHeight = Math.Min(2.5, Math.Max(1.0, tableType.DefaultBandHeight * 0.25));
        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            var cy = view.BandCenterYAt(i);
            AppendCenteredLabel(tr, modelSpace, view, new Point3d(labelCenterX, cy, 0),
                band.Title, titleHeight, LayerText, band.TextAci);
        }

        var elevLabelX = xLeft - Math.Max(6.0, labelW * 0.15);
        var topBandH = bands.Count > 0 ? bands[0].Height : tableType.DefaultBandHeight;

        AppendLabel(tr, modelSpace, view,
            new Point3d(elevLabelX, yGraphTop + Math.Max(1.0, topBandH * 0.25), 0),
            $"1:{view.HorizontalDenom:0}/{view.VerticalDenom:0}",
            Math.Max(1.5, topBandH * 0.2), LayerText, 7, 0);

        AppendLabel(tr, modelSpace, view,
            new Point3d((xData + xRight) * 0.5, yGraphTop + Math.Max(1.5, topBandH * 0.25), 0),
            string.IsNullOrWhiteSpace(view.TableName) ? $"Poduzni — {view.AxisName}" : view.TableName,
            Math.Max(1.5, topBandH * 0.22), LayerText, 7, 0);

        DrawElevationGrid(tr, modelSpace, view, xData, xRight, yGraphBot, yGraphTop, elevLabelX);
        DrawStationColumns(tr, modelSpace, view, terrainSamples, y0, yGraphTop);
        DrawMainElementsBands(tr, modelSpace, view);
        DrawTerrainPolyline(tr, modelSpace, view, terrainSamples);
    }

    public static ObjectId DrawTerrainPolyline(
        Transaction tr,
        BlockTableRecord modelSpace,
        ProfileViewData view,
        IReadOnlyList<(double Station, double Elevation)> samples)
    {
        EnsureLayers(tr, modelSpace.Database);
        if (samples.Count < 2)
        {
            return ObjectId.Null;
        }

        var filtered = samples
            .Where(s => s.Station >= view.StartStation - 1e-6 && s.Station <= view.EndStation + 1e-6)
            .ToList();
        if (filtered.Count < 2)
        {
            filtered = samples.ToList();
        }

        var pl = new Polyline(filtered.Count)
        {
            Layer = LayerTerrain,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, 3),
            Elevation = 0
        };

        for (var i = 0; i < filtered.Count; i++)
        {
            var p = view.ToProfilePoint(filtered[i].Station, filtered[i].Elevation);
            pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
        }

        modelSpace.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        ProfileXData.AttachTerrain(pl, view.ProfileId);
        return pl.ObjectId;
    }

    /// <summary>Kompatibilnost — isto što i DrawFullProfile bez duplog terena.</summary>
    public static void DrawTable(
        Transaction tr,
        BlockTableRecord modelSpace,
        ProfileViewData view,
        IReadOnlyList<(double Station, double Elevation)>? terrainSamples)
    {
        DrawFullProfile(tr, modelSpace, view, terrainSamples ?? Array.Empty<(double, double)>());
    }

    public static int EraseProfileEntities(Transaction tr, Database db, string profileId, string? roleFilter = null)
    {
        var count = 0;
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                continue;
            }

            if (!ProfileXData.TryReadRole(entity, out var role, out var pid) ||
                !string.Equals(pid, profileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (roleFilter is not null &&
                !string.Equals(role, roleFilter, StringComparison.Ordinal))
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Erase();
            count++;
        }

        return count;
    }

    private static void DrawElevationGrid(
        Transaction tr,
        BlockTableRecord ms,
        ProfileViewData view,
        double x0,
        double x1,
        double yGraphBot,
        double yGraphTop,
        double labelX)
    {
        var elevStep = SuggestElevStep(view.TopElevation - view.BaseElevation);

        // Horizontalne — TCM_POD_MREZA
        for (var e = view.BaseElevation; e <= view.TopElevation + 1e-6; e += elevStep)
        {
            var y = view.ElevationToY(e);
            AppendHLine(tr, ms, view, x0, x1, y, LayerGrid);
            AppendLabel(tr, ms, view, new Point3d(labelX, y, 0),
                e.ToString("0.#"), Math.Max(0.7, elevStep * view.ElevationFactor * 0.35),
                LayerText, 7, 0);
        }

        // Poprečne (vertikalne u grafiku) — TCM_POP_MREZA
        foreach (var s in view.CollectTabulationStations())
        {
            var x = view.StationToX(s);
            AppendVLine(tr, ms, view, x, yGraphBot, yGraphTop, LayerCrossGrid);
        }
    }

    private static void DrawStationColumns(
        Transaction tr,
        BlockTableRecord ms,
        ProfileViewData view,
        IReadOnlyList<(double Station, double Elevation)> samples,
        double yBottom,
        double yTop)
    {
        if (!view.DrawTabulation)
        {
            return;
        }

        var stations = view.CollectTabulationStations();
        var stepHint = stations.Count >= 2
            ? Math.Max(stations[1] - stations[0], 1e-6)
            : Math.Max(view.StationTickInterval, 1e-6);
        var textH = Math.Max(0.6, Math.Min(2.2, stepHint * view.StationFactor * 0.35));
        var bands = view.ResolveBands();

        List<(double Station, double Elevation)> gradeSamples = [];
        ProfileGradeSampler.TryLoadSamples(tr, ms.Database, view.AxisName, out gradeSamples);

        // Plave linije samo u grafiku: od početka tabele (GraphBottomY) nagore do terena.
        // Ne ulaze u rubrike (ni do kote terena u tabeli).
        _ = yBottom;
        var yTableTop = view.GraphBottomY;

        double? prevStation = null;
        foreach (var s in stations)
        {
            var x = view.StationToX(s);
            if (view.DrawVerticals)
            {
                var elev = InterpolateElevation(samples, s) ?? view.BaseElevation;
                var yTerrain = Math.Min(yTop, Math.Max(yTableTop, view.ElevationToY(elev)));
                if (yTerrain > yTableTop + 1e-6)
                {
                    AppendVLine(tr, ms, view, x, yTableTop, yTerrain, LayerVertical);
                }
            }

            var gap = prevStation is null ? 0.0 : s - prevStation.Value;
            var z = InterpolateElevation(samples, s);
            var zGrade = InterpolateElevation(gradeSamples, s);

            for (var i = 0; i < bands.Count; i++)
            {
                var band = bands[i];
                var cy = view.BandCenterYAt(i);
                switch (band.Kind)
                {
                    case ProfileBandKind.ProfileMarks:
                        if (prevStation is not null)
                        {
                            var midX = view.StationToX((prevStation.Value + s) * 0.5);
                            AppendRotatedText(tr, ms, view,
                                new Point3d(midX, cy, 0),
                                gap.ToString("0.000"), textH * 0.85, LayerText, band.TextAci);
                        }

                        break;

                    case ProfileBandKind.Stations:
                        AppendRotatedText(tr, ms, view,
                            new Point3d(x, cy, 0),
                            ChainageFormatter.Format(s, ChainageFormatter.DefaultFormat),
                            textH, LayerText, band.TextAci);
                        break;

                    case ProfileBandKind.TerrainElevations when z is not null:
                        AppendRotatedText(tr, ms, view,
                            new Point3d(x, cy, 0),
                            z.Value.ToString("0.00"), textH, LayerText, band.TextAci);
                        break;

                    case ProfileBandKind.GradeElevations when zGrade is not null:
                        AppendRotatedText(tr, ms, view,
                            new Point3d(x, cy, 0),
                            zGrade.Value.ToString("0.00"), textH, LayerText, band.TextAci);
                        break;

                    case ProfileBandKind.LaneWidths:
                        if (ProfileLaneWidthStore.TryGetAtStation(view.AxisName, s, out var leftW, out var rightW))
                        {
                            AppendRotatedText(tr, ms, view,
                                new Point3d(x, cy, 0),
                                $"L {leftW:0.##}  D {rightW:0.##}",
                                textH * 0.9, LayerText, band.TextAci);
                        }

                        break;
                }
            }

            prevStation = s;
        }
    }

    /// <summary>
    /// Glavni elementi (CGSA): pravac + dužina/stac.; luk nagore ili nadole prema smeru (CW/CCW).
    /// </summary>
    private static void DrawMainElementsBands(
        Transaction tr,
        BlockTableRecord ms,
        ProfileViewData view)
    {
        var bands = view.ResolveBands();
        var indices = new List<int>();
        for (var i = 0; i < bands.Count; i++)
        {
            if (bands[i].Kind == ProfileBandKind.MainElements)
            {
                indices.Add(i);
            }
        }

        if (indices.Count == 0)
        {
            return;
        }

        var axis = AxisGeometryReader.ReadAxis(tr, ms.Database, view.AxisName, view.StartStation);
        if (axis is null || axis.Elements.Count == 0)
        {
            return;
        }

        var start = view.StartStation;
        var end = view.EndStation;
        if (end <= start + 1e-6)
        {
            return;
        }

        foreach (var bandIndex in indices)
        {
            var band = bands[bandIndex];
            var yBot = view.BandBottomYAt(bandIndex);
            var h = Math.Max(1.0, band.Height);
            // Baza i ekstrem: “gore” luk (leva/CCW) i “dole” luk (desna/CW).
            var yBaseLow = yBot + h * 0.28;
            var yBaseHigh = yBot + h * 0.72;
            var yPeakUp = yBot + h * 0.90;
            var yPeakDown = yBot + h * 0.10;
            var textH = Math.Max(0.7, Math.Min(2.0, h * 0.16));
            var aciLine = band.TextAci < 1 ? (short)7 : band.TextAci;
            const short aciArcLabel = 1;

            // Podrazumevana baza pravca: iz prvog/susednog luka.
            // CW → luk nagore; CCW → luk nadole.
            var defaultBulgeUp = true;
            foreach (var el in axis.Elements)
            {
                if (el.Type == AlignmentElementType.Arc)
                {
                    defaultBulgeUp = el.Clockwise;
                    break;
                }
            }

            for (var ei = 0; ei < axis.Elements.Count; ei++)
            {
                var element = axis.Elements[ei];
                var s0 = Math.Max(start, element.StartStation);
                var s1 = Math.Min(end, element.EndStation);
                if (s1 <= s0 + 1e-6)
                {
                    continue;
                }

                var x0 = view.StationToX(s0);
                var x1 = view.StationToX(s1);
                if (Math.Abs(x1 - x0) < 1e-6)
                {
                    continue;
                }

                if (element.Type == AlignmentElementType.Tangent)
                {
                    var bulgeUp = ResolveBulgeUpNear(axis.Elements, ei, defaultBulgeUp); // CW=gore / CCW=dole
                    var yLine = bulgeUp ? yBaseLow : yBaseHigh;
                    AppendSegment(tr, ms, view,
                        new Point3d(x0, yLine, 0), new Point3d(x1, yLine, 0), aciLine);

                    var length = Math.Abs(element.EndStation - element.StartStation);
                    if (Math.Abs(s0 - element.StartStation) > 1e-3 ||
                        Math.Abs(s1 - element.EndStation) > 1e-3)
                    {
                        length = s1 - s0;
                    }

                    var midX = (x0 + x1) * 0.5;
                    var lengthY = bulgeUp ? yLine + textH * 0.55 : yLine - textH * 0.55;
                    AppendCenteredLabel(tr, ms, view,
                        new Point3d(midX, lengthY, 0),
                        length.ToString("0.00"),
                        textH, LayerText, aciLine);

                    var st0 = ChainageFormatter.Format(element.StartStation, ChainageFormatter.DefaultFormat);
                    var st1 = ChainageFormatter.Format(element.EndStation, ChainageFormatter.DefaultFormat);
                    var stY0 = bulgeUp ? yLine + textH * 1.35 : yLine - textH * 2.35;
                    var stY1 = bulgeUp ? yLine + textH * 2.35 : yLine - textH * 1.35;
                    AppendLabel(tr, ms, view,
                        new Point3d(x0 + textH * 0.15, stY0, 0),
                        st0, textH * 0.85, LayerText, aciLine, 0);
                    AppendLabel(tr, ms, view,
                        new Point3d(x0 + textH * 0.15, stY1, 0),
                        st1, textH * 0.85, LayerText, aciLine, 0);
                }
                else
                {
                    // CW → luk nagore; CCW → luk nadole.
                    var bulgeUp = element.Clockwise;
                    var yLine = bulgeUp ? yBaseLow : yBaseHigh;
                    var yPeak = bulgeUp ? yPeakUp : yPeakDown;
                    var pts = BuildArcBandPoints(x0, x1, yLine, yPeak, 28);
                    AppendPoly(tr, ms, view, pts, aciLine);

                    var arcLen = Math.Abs(element.EndStation - element.StartStation);
                    var radius = Math.Abs(element.Radius);
                    var midX = (x0 + x1) * 0.5;
                    // L/R u “udubljenju” luka (ispod gore-luka / iznad dole-luka).
                    var labelY = bulgeUp
                        ? Math.Max(yBot + textH * 0.35, yLine - textH * 0.2)
                        : Math.Min(yBot + h - textH * 0.35, yLine + textH * 0.2);

                    AppendCenteredLabel(tr, ms, view,
                        new Point3d(midX, labelY, 0),
                        $"L={arcLen:0.00} R={radius:0.00}",
                        textH, LayerText, aciArcLabel);
                }
            }
        }
    }

    /// <summary>Baza pravca prati smer susednog luka.</summary>
    private static bool ResolveBulgeUpNear(
        IReadOnlyList<AlignmentElement> elements,
        int index,
        bool fallback)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (elements[i].Type == AlignmentElementType.Arc)
            {
                return elements[i].Clockwise;
            }
        }

        for (var i = index + 1; i < elements.Count; i++)
        {
            if (elements[i].Type == AlignmentElementType.Arc)
            {
                return elements[i].Clockwise;
            }
        }

        return fallback;
    }

    private static List<Point3d> BuildArcBandPoints(
        double x0, double x1, double yBase, double yPeak, int segments)
    {
        var pts = new List<Point3d>(segments + 1);
        var chord = x1 - x0;
        var amp = yPeak - yBase;
        if (Math.Abs(chord) < 1e-9)
        {
            pts.Add(new Point3d(x0, yBase, 0));
            return pts;
        }

        for (var i = 0; i <= segments; i++)
        {
            var t = i / (double)segments;
            var x = x0 + chord * t;
            var y = yBase + amp * Math.Sin(Math.PI * t);
            pts.Add(new Point3d(x, y, 0));
        }

        return pts;
    }

    private static void AppendSegment(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        Point3d a, Point3d b, short aci)
    {
        var line = new Line(a, b)
        {
            Layer = LayerFrame,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci)
        };
        ms.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        ProfileXData.AttachTable(line, view.ProfileId);
    }

    private static void AppendPoly(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        IReadOnlyList<Point3d> pts, short aci)
    {
        if (pts.Count < 2)
        {
            return;
        }

        var pl = new Polyline(pts.Count)
        {
            Layer = LayerFrame,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci),
            Elevation = 0
        };
        for (var i = 0; i < pts.Count; i++)
        {
            pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, 0, 0);
        }

        ms.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        ProfileXData.AttachTable(pl, view.ProfileId);
    }

    private static double SuggestElevStep(double range)
    {
        if (range <= 5)
        {
            return 1;
        }

        if (range <= 15)
        {
            return 1;
        }

        if (range <= 40)
        {
            return 2;
        }

        return 5;
    }

    private static double? InterpolateElevation(
        IReadOnlyList<(double Station, double Elevation)> samples,
        double station)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        if (station <= samples[0].Station)
        {
            return samples[0].Elevation;
        }

        if (station >= samples[^1].Station)
        {
            return samples[^1].Elevation;
        }

        for (var i = 0; i < samples.Count - 1; i++)
        {
            var a = samples[i];
            var b = samples[i + 1];
            if (station < a.Station - 1e-9 || station > b.Station + 1e-9)
            {
                continue;
            }

            var t = Math.Abs(b.Station - a.Station) < 1e-9
                ? 0
                : (station - a.Station) / (b.Station - a.Station);
            return a.Elevation + t * (b.Elevation - a.Elevation);
        }

        return null;
    }

    private static void AppendRect(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        double x0, double y0, double x1, double y1, string layer)
    {
        var pl = new Polyline(4) { Closed = true, Layer = layer, Elevation = 0 };
        pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
        pl.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
        pl.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
        pl.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
        ms.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        ProfileXData.AttachView(pl, view.ProfileId, view.AxisName);
    }

    private static void AppendHLine(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        double x0, double x1, double y, string layer)
    {
        var line = new Line(new Point3d(x0, y, 0), new Point3d(x1, y, 0)) { Layer = layer };
        ms.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        ProfileXData.AttachTable(line, view.ProfileId);
    }

    private static void AppendVLine(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        double x, double y0, double y1, string layer)
    {
        var line = new Line(new Point3d(x, y0, 0), new Point3d(x, y1, 0)) { Layer = layer };
        ms.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        ProfileXData.AttachTable(line, view.ProfileId);
    }

    private static void AppendLabel(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        Point3d position, string content, double height, string layer, short aci, double rotationDeg)
    {
        var text = new DBText
        {
            Position = position,
            Height = Math.Max(0.2, height),
            TextString = content,
            Layer = layer,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci),
            Rotation = rotationDeg * Math.PI / 180.0
        };
        ms.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
        ProfileXData.AttachTable(text, view.ProfileId);
    }

    private static void AppendCenteredLabel(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        Point3d center, string content, double height, string layer, short aci)
    {
        var text = new DBText
        {
            Position = center,
            Height = Math.Max(0.2, height),
            TextString = content,
            Layer = layer,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci),
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = center
        };
        ms.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
        text.AlignmentPoint = center;
        ProfileXData.AttachTable(text, view.ProfileId);
    }

    private static void AppendRotatedText(
        Transaction tr, BlockTableRecord ms, ProfileViewData view,
        Point3d center, string content, double height, string layer, short aci)
    {
        // Vertical text (90°), origin near center of column.
        var text = new DBText
        {
            Position = center,
            Height = Math.Max(0.25, height),
            TextString = content,
            Layer = layer,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci),
            Rotation = Math.PI / 2.0,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = center
        };
        ms.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
        // Alignment modes require AlignmentPoint after Append sometimes — re-set:
        text.AlignmentPoint = center;
        ProfileXData.AttachTable(text, view.ProfileId);
    }

    private static void EnsureLayer(Transaction tr, Database db, string name, short aci)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(name))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci)
        };
        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }
}
