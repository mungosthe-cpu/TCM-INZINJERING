using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal static class TerrainProjectionRefresh
{
    /// <summary>
    /// Briše staru 3D poliliniju i ponovo projektuje osu na sačuvani teren.
    /// Broj tačaka se skalira proporcionalno novoj dužini ose.
    /// </summary>
    public static int RefreshIfExists(Transaction tr, Database db, string axisName)
    {
        if (!TerrainProjectionStore.TryLoad(tr, db, axisName, out var options, out var referenceLength, out var terrainIds))
        {
            return 0;
        }

        var metadata = RoadAxisStore.Load(tr, db, axisName);
        var startStation = metadata?.StartStation ?? 0;
        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
        if (axis is null || axis.Elements.Count == 0)
        {
            return 0;
        }

        var currentLength = axis.TotalLength;
        if (referenceLength < 1e-6)
        {
            referenceLength = Math.Max(currentLength, 1e-6);
        }

        var scaledCount = TerrainProjectionStore.ScalePointCount(
            options.PointCount,
            referenceLength,
            currentLength);

        var scaledOptions = new TerrainSamplingOptions
        {
            Mode = options.Mode,
            PointCount = scaledCount
        };

        var terrain = TerrainMeshBuilder.Build(tr, terrainIds);
        if (!terrain.HasTerrain)
        {
            return 0;
        }

        var projection = TerrainProjector.ProjectRoadAxis(axis, terrain, scaledOptions);
        if (projection.Points.Count < 2)
        {
            return 0;
        }

        DeleteProjectedAxes(tr, db, axisName);

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);
        var id = RoadDrawing.DrawProjectedAxis(tr, modelSpace, axisName, projection.Points);
        if (id.IsNull)
        {
            return 0;
        }

        RoadDrawing.SendProjectedAxisBelowPickables(tr, db, id, axisName);
        return 1;
    }

    public static void DeleteProjectedAxes(Transaction tr, Database db, string axisName)
    {
        RoadDrawing.RunWithUnlockedProjectedAxisLayer(tr, db, () =>
        {
            var modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            var toErase = new List<ObjectId>();
            foreach (ObjectId id in modelSpace)
            {
                if (id.IsErased)
                {
                    continue;
                }

                var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
                if (!RoadXData.TryReadProjectedAxis(entity, out var name))
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
        });
    }
}

