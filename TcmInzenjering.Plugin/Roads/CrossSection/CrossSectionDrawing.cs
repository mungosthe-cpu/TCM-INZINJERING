using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Roads.CrossSection;

/// <summary>Crtanje poprečnih profila (section views) iz pop. osa + TIN.</summary>
internal static class CrossSectionDrawing
{
    public const string LayerFrame = "TCM_POP_PRF_OKVIR";
    public const string LayerTerrain = "TCM_POP_PRF_TEREN";
    public const string LayerGrade = "TCM_POP_PRF_NIV";
    public const string LayerText = "TCM_POP_PRF_TEKST";
    public const string RoleSection = "POPPRF";

    public static void EnsureLayers(Transaction tr, Database db)
    {
        EnsureLayer(tr, db, LayerFrame, 7);
        EnsureLayer(tr, db, LayerTerrain, 3);
        EnsureLayer(tr, db, LayerGrade, 1);
        EnsureLayer(tr, db, LayerText, 7);
    }

    /// <summary>
    /// Crta jedan poprečni profil: offset od -left do +right, Z u vertikalnoj razmeri.
    /// </summary>
    public static void DrawSection(
        Transaction tr,
        BlockTableRecord modelSpace,
        Point3d origin,
        string sectionId,
        string title,
        IReadOnlyList<(double Offset, double Elevation)> terrainSamples,
        double? gradeElevation,
        double leftWidth,
        double rightWidth,
        double baseElevation,
        double topElevation,
        double horizontalScale,
        double verticalScale)
    {
        EnsureLayers(tr, modelSpace.Database);
        RoadDrawing.EnsureRegApp(tr, modelSpace.Database);

        var width = (leftWidth + rightWidth) * horizontalScale;
        var height = Math.Max(1.0, (topElevation - baseElevation) * verticalScale);
        var x0 = origin.X;
        var y0 = origin.Y;
        var xRight = x0 + width;
        var yTop = y0 + height;

        // Okvir
        var frame = new Polyline(4)
        {
            Layer = LayerFrame,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, 7),
            Closed = true
        };
        frame.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
        frame.AddVertexAt(1, new Point2d(xRight, y0), 0, 0, 0);
        frame.AddVertexAt(2, new Point2d(xRight, yTop), 0, 0, 0);
        frame.AddVertexAt(3, new Point2d(x0, yTop), 0, 0, 0);
        modelSpace.AppendEntity(frame);
        tr.AddNewlyCreatedDBObject(frame, true);
        Attach(frame, sectionId);

        // Osovina (offset 0)
        var xAxis = OffsetToX(origin, 0, leftWidth, horizontalScale);
        AppendVLine(tr, modelSpace, sectionId, xAxis, y0, yTop, LayerFrame, 8);

        // Naslov
        var titleText = new DBText
        {
            Position = new Point3d(x0, yTop + 1.5, 0),
            Height = Math.Max(1.2, height * 0.08),
            TextString = title,
            Layer = LayerText,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, 7)
        };
        modelSpace.AppendEntity(titleText);
        tr.AddNewlyCreatedDBObject(titleText, true);
        Attach(titleText, sectionId);

        // Teren
        if (terrainSamples.Count >= 2)
        {
            var pl = new Polyline(terrainSamples.Count)
            {
                Layer = LayerTerrain,
                Color = AcColor.FromColorIndex(ColorMethod.ByAci, 3),
                Elevation = 0
            };
            for (var i = 0; i < terrainSamples.Count; i++)
            {
                var s = terrainSamples[i];
                var x = OffsetToX(origin, s.Offset, leftWidth, horizontalScale);
                var y = ElevationToY(origin, s.Elevation, baseElevation, verticalScale);
                pl.AddVertexAt(i, new Point2d(x, y), 0, 0, 0);
            }

            modelSpace.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            Attach(pl, sectionId);
        }

        // Niveleta (horizontalna linija na koti + rubovi kolovoza)
        if (gradeElevation is not null)
        {
            var yG = ElevationToY(origin, gradeElevation.Value, baseElevation, verticalScale);
            var xL = OffsetToX(origin, -leftWidth, leftWidth, horizontalScale);
            var xR = OffsetToX(origin, rightWidth, leftWidth, horizontalScale);
            var gradeLine = new Line(
                new Point3d(xL, yG, 0),
                new Point3d(xR, yG, 0))
            {
                Layer = LayerGrade,
                Color = AcColor.FromColorIndex(ColorMethod.ByAci, 1)
            };
            modelSpace.AppendEntity(gradeLine);
            tr.AddNewlyCreatedDBObject(gradeLine, true);
            Attach(gradeLine, sectionId);
        }
    }

    public static double OffsetToX(Point3d origin, double offset, double leftWidth, double horizontalScale) =>
        origin.X + (offset + leftWidth) * horizontalScale;

    public static double ElevationToY(Point3d origin, double elevation, double baseElevation, double verticalScale) =>
        origin.Y + (elevation - baseElevation) * verticalScale;

    private static void AppendVLine(
        Transaction tr,
        BlockTableRecord ms,
        string sectionId,
        double x,
        double y0,
        double y1,
        string layer,
        short aci)
    {
        var line = new Line(new Point3d(x, y0, 0), new Point3d(x, y1, 0))
        {
            Layer = layer,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci)
        };
        ms.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        Attach(line, sectionId);
    }

    private static void Attach(Entity entity, string sectionId)
    {
        entity.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleSection),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, sectionId));
    }

    private static void EnsureLayer(Transaction tr, Database db, string name, short aci)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name))
        {
            return;
        }

        lt.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = AcColor.FromColorIndex(ColorMethod.ByAci, aci)
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }
}
