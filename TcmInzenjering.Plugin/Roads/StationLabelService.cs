using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal static class StationLabelService
{
    public static int RefreshAxis(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            return 0;
        }

        AxisGeometryUpdater.Reconcile(
            tr,
            db,
            axisName,
            metadata.CurveRadius,
            metadata.StartStation);

        return RefreshLabels(tr, db, axisName, metadata);
    }

    public static int RefreshLabels(Transaction tr, Database db, string axisName)
    {
        var metadata = RoadAxisStore.Load(tr, db, axisName);
        if (metadata is null)
        {
            return 0;
        }

        return RefreshLabels(tr, db, axisName, metadata);
    }

    private static int RefreshLabels(Transaction tr, Database db, string axisName, RoadAxisMetadata metadata)
    {
        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, metadata.StartStation);
        if (axis is null)
        {
            return 0;
        }

        DeleteLabels(tr, db, axisName);
        DeleteRadiusLabels(tr, db, axisName);
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var stationCount = RoadDrawing.DrawStationLabels(
            tr,
            modelSpace,
            axis,
            metadata.ToLabelOptions());

        var radiusCount = RoadDrawing.DrawRadiusLabels(
            tr,
            modelSpace,
            axis,
            metadata.TextHeight,
            metadata.LabelSideSign);

        return stationCount + radiusCount;
    }

    public static void DeleteRadiusLabels(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer != RoadDrawing.RadiusLayerName ||
                !RoadXData.TryReadRadiusAnnotation(entity, out var name, out _))
            {
                continue;
            }

            if (string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            entity.Erase();
        }
    }

    public static void DeleteLabels(Transaction tr, Database db, string axisName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
            if (entity.Layer != RoadDrawing.StationLayerName ||
                !RoadXData.TryReadStationLabel(entity, out var name, out _, out _))
            {
                continue;
            }

            if (string.Equals(name, axisName, StringComparison.OrdinalIgnoreCase))
            {
                toErase.Add(id);
            }
        }

        foreach (var id in toErase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            entity.Erase();
        }
    }
}
