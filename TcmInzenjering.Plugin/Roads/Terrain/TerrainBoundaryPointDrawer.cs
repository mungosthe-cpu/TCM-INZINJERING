using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Dialogs;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>XData marker za tačke granice (blok ili DBPoint).</summary>
internal static class TerrainBoundaryPointXData
{
    public const string Role = "TER_BOUND_PT";

    public static void Attach(Entity entity, string surfaceName)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        entity.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, Role),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceName.Trim()));
    }

    public static bool TryRead(Entity entity, out string? surfaceName)
    {
        surfaceName = null;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        var sawRole = false;
        foreach (var item in items)
        {
            if (item.TypeCode != (int)DxfCode.ExtendedDataAsciiString)
            {
                continue;
            }

            var s = Convert.ToString(item.Value);
            if (!sawRole)
            {
                if (string.Equals(s, Role, StringComparison.Ordinal))
                {
                    sawRole = true;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(s))
            {
                surfaceName = s.Trim();
                return true;
            }
        }

        return sawRole;
    }
}

/// <summary>
/// Crta tačke granice kao isti blok (TCMTERBLOK mapiranje) ili DBPoint.
/// </summary>
internal static class TerrainBoundaryPointDrawer
{
    public sealed class SyncResult
    {
        public int Drawn { get; init; }
        public bool UsedBlock { get; init; }
        public string StyleLabel { get; init; } = "DBPoint";
    }

    public static SyncResult Sync(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        string granicaSurfaceName,
        IReadOnlyList<Point3d> points)
    {
        RoadDrawing.EnsureRegApp(tr, db);
        EraseExisting(tr, db, granicaSurfaceName);

        if (points.Count == 0)
        {
            return new SyncResult();
        }

        var layer = TerrainLayerNames.For(RoadCommands.TerrainLayerName, granicaSurfaceName);
        EnsureLayer(tr, db, layer, 3);

        TerrainPointBlockPreferences.Load();
        var mapping = TerrainPointBlockPreferences.Current;
        ObjectId blockDefId = ObjectId.Null;
        if (mapping is not null)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(mapping.BlockName))
            {
                blockDefId = bt[mapping.BlockName];
            }
        }

        var usedBlock = !blockDefId.IsNull;
        var drawn = 0;
        foreach (var p in points)
        {
            if (usedBlock)
            {
                if (TryInsertBlock(
                        tr, modelSpace, blockDefId, mapping!, p, layer,
                        granicaSurfaceName, out _))
                {
                    drawn++;
                    continue;
                }
            }

            var pt = new DBPoint(p)
            {
                Layer = layer,
                Color = AcColor.FromColorIndex(ColorMethod.ByAci, 3)
            };
            modelSpace.AppendEntity(pt);
            tr.AddNewlyCreatedDBObject(pt, true);
            TerrainBoundaryPointXData.Attach(pt, granicaSurfaceName);
            drawn++;
        }

        return new SyncResult
        {
            Drawn = drawn,
            UsedBlock = usedBlock,
            StyleLabel = usedBlock ? $"blok {mapping!.BlockName}" : "DBPoint"
        };
    }

    /// <summary>
    /// Ubacuje tačku terena u istom formatu kao postojeće tačke na crtežu:
    /// TCMTERBLOK blok sa atributom visine ako je definisan, inače DBPoint.
    /// Bez granica-XData — tačka pripada baznom terenu.
    /// </summary>
    public static (bool UsedBlock, string StyleLabel, long Handle) InsertTerrainPoint(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        Point3d position,
        string layer)
    {
        TerrainPointBlockPreferences.Load();
        var mapping = TerrainPointBlockPreferences.Current;
        if (mapping is not null)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(mapping.BlockName) &&
                TryInsertBlock(tr, modelSpace, bt[mapping.BlockName], mapping,
                    position, layer, granicaSurfaceName: null, out var blockHandle))
            {
                return (true, $"blok {mapping.BlockName}", blockHandle);
            }
        }

        var pt = new DBPoint(position) { Layer = layer };
        modelSpace.AppendEntity(pt);
        tr.AddNewlyCreatedDBObject(pt, true);
        return (false, "DBPoint", pt.Handle.Value);
    }

    private static bool TryInsertBlock(
        Transaction tr,
        BlockTableRecord modelSpace,
        ObjectId blockDefId,
        TerrainBlockPointMapping mapping,
        Point3d position,
        string layer,
        string? granicaSurfaceName,
        out long handle)
    {
        handle = 0;
        try
        {
            var br = new BlockReference(position, blockDefId)
            {
                Layer = layer
            };
            modelSpace.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            // Attribute definitions → references
            var btr = (BlockTableRecord)tr.GetObject(blockDefId, OpenMode.ForRead);
            if (btr.HasAttributeDefinitions)
            {
                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition attDef ||
                        attDef.Constant)
                    {
                        continue;
                    }

                    var attRef = new AttributeReference();
                    attRef.SetAttributeFromBlock(attDef, br.BlockTransform);
                    if (string.Equals(attDef.Tag, mapping.ElevationAttributeTag,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        attRef.TextString = position.Z.ToString(
                            "0.##",
                            System.Globalization.CultureInfo.InvariantCulture);
                    }

                    br.AttributeCollection.AppendAttribute(attRef);
                    tr.AddNewlyCreatedDBObject(attRef, true);
                }
            }

            if (granicaSurfaceName is not null)
            {
                TerrainBoundaryPointXData.Attach(br, granicaSurfaceName);
            }

            handle = br.Handle.Value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EraseExisting(Transaction tr, Database db, string granicaSurfaceName)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);
        var toErase = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.IsErased)
            {
                continue;
            }

            if (!TerrainBoundaryPointXData.TryRead(entity, out var name))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, granicaSurfaceName, StringComparison.OrdinalIgnoreCase))
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
