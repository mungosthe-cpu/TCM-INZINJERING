using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>
/// Posle pomeranja nivelete / osvežene 3D projekcije — ponovo crta tabelu i teren podužnog profila.
/// </summary>
internal static class ProfileViewRefresh
{
    public static int RefreshIfExists(Transaction tr, Database db, string axisName)
    {
        var views = ProfileViewStore.LoadAllForAxis(tr, db, axisName);
        if (views.Count == 0)
        {
            return 0;
        }

        if (!ProfileProjectedSampler.TryLoadSamples(tr, db, axisName, out var samples) ||
            samples.Count < 2)
        {
            return 0;
        }

        var metadata = RoadAxisStore.Load(tr, db, axisName);
        var metaInterval = metadata?.Interval ?? 0;
        var startStation = metadata?.StartStation ?? views[0].StartStation;
        var endStation = metadata?.EndStation ?? (startStation + (samples[^1].Station - samples[0].Station));

        var axis = AxisGeometryReader.ReadAxis(tr, db, axisName, startStation);
        if (axis is not null && axis.Elements.Count > 0)
        {
            startStation = axis.StartStation;
            endStation = axis.StartStation + axis.TotalLength;
        }

        var minZ = samples.Min(s => s.Elevation);
        var maxZ = samples.Max(s => s.Elevation);

        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var refreshed = 0;
        foreach (var old in views)
        {
            var baseElev = old.BaseElevation;
            var topElev = old.TopElevation;
            // Proširi opseg ako teren izađe van okvira; zadrži korisničke kotе ako su šire.
            baseElev = Math.Min(baseElev, Math.Floor(minZ));
            topElev = Math.Max(topElev, Math.Ceiling(maxZ + 1.0));

            var updated = new ProfileViewData
            {
                ProfileId = old.ProfileId,
                AxisName = old.AxisName,
                TableName = old.TableName,
                TableType = old.TableType,
                Origin = old.Origin,
                StartStation = startStation,
                EndStation = endStation,
                BaseElevation = baseElev,
                TopElevation = topElev,
                HorizontalDenom = old.HorizontalDenom,
                VerticalDenom = old.VerticalDenom,
                StationTickInterval = old.StationTickInterval,
                TabulationMode = old.TabulationMode,
                CrossAxisInterval = metaInterval > 1e-6 ? metaInterval : old.CrossAxisInterval,
                BetweenDivisor = old.BetweenDivisor,
                DrawVerticals = old.DrawVerticals,
                DrawTabulation = old.DrawTabulation
            };

            ProfileDrawing.EraseProfileEntities(tr, db, old.ProfileId);
            ProfileViewStore.Save(tr, db, updated);
            ProfileDrawing.DrawFullProfile(tr, modelSpace, updated, samples);
            refreshed++;
        }

        return refreshed;
    }
}
