using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Posle MOVE/STRETCH izohipse — pomeri kotne oznake na istu udaljenost duž konture.
/// </summary>
internal static class ContourLabelMonitor
{
    private static readonly HashSet<long> DirtyContourHandles = new();
    private static bool _idleHooked;
    private static int _suppressDepth;

    public static void Initialize()
    {
        AcApp.DocumentManager.DocumentCreated += OnDocumentCreated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            Attach(doc);
        }
    }

    public static void Terminate()
    {
        AcApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
        foreach (Document doc in AcApp.DocumentManager)
        {
            Detach(doc);
        }
    }

    private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) =>
        Attach(e.Document);

    private static void Attach(Document doc)
    {
        Detach(doc);
        doc.Database.ObjectModified += OnObjectModified;
        doc.CommandEnded += OnCommandEnded;
        doc.CommandCancelled += OnCommandEnded;
        doc.CommandFailed += OnCommandEnded;
    }

    private static void Detach(Document doc)
    {
        doc.Database.ObjectModified -= OnObjectModified;
        doc.CommandEnded -= OnCommandEnded;
        doc.CommandCancelled -= OnCommandEnded;
        doc.CommandFailed -= OnCommandEnded;
    }

    private static void OnObjectModified(object sender, ObjectEventArgs e)
    {
        if (TerrainCommandGuard.IsSuppressed || _suppressDepth > 0 || e.DBObject is not Entity entity)
        {
            return;
        }

        if (!TerrainContourXData.IsContour(entity))
        {
            return;
        }

        lock (DirtyContourHandles)
        {
            DirtyContourHandles.Add(entity.Handle.Value);
        }

        EnsureIdle();
    }

    private static void OnCommandEnded(object sender, CommandEventArgs e)
    {
        if (TerrainCommandGuard.IsSuppressed ||
            e.GlobalCommandName.StartsWith("TCM", StringComparison.OrdinalIgnoreCase))
        {
            lock (DirtyContourHandles)
            {
                DirtyContourHandles.Clear();
            }

            return;
        }

        EnsureIdle();
    }

    private static void EnsureIdle()
    {
        if (TerrainCommandGuard.IsSuppressed || _idleHooked)
        {
            return;
        }

        _idleHooked = true;
        AcApp.Idle += OnIdle;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        long[] handles;
        lock (DirtyContourHandles)
        {
            AcApp.Idle -= OnIdle;
            _idleHooked = false;
            if (TerrainCommandGuard.IsSuppressed || DirtyContourHandles.Count == 0)
            {
                DirtyContourHandles.Clear();
                return;
            }

            handles = DirtyContourHandles.ToArray();
            DirtyContourHandles.Clear();
        }

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !string.IsNullOrEmpty(doc.CommandInProgress))
        {
            return;
        }

        try
        {
            _suppressDepth++;
            using var docLock = doc.LockDocument();
            Transaction? tr = null;
            try
            {
                tr = doc.Database.TransactionManager.StartTransaction();
                var updated = ContourLabelGeometry.SyncLabelsForContours(tr, doc.Database, handles);
                tr.Commit();
                tr = null;
                if (updated > 0)
                {
                    doc.Editor.WriteMessage(
                        $"\nTCM-INZINJERING: Kotne oznake usklađene sa izohipsama ({updated}).");
                }
            }
            catch
            {
                try
                {
                    tr?.Abort();
                }
                catch
                {
                    // ignore
                }
            }
            finally
            {
                try
                {
                    tr?.Dispose();
                }
                catch
                {
                    // eInvalidContext na Dispose
                }
            }
        }
        catch
        {
            // best-effort
        }
        finally
        {
            _suppressDepth--;
        }
    }
}

/// <summary>Geometrija i sync kotnih oznaka na izohipsama.</summary>
internal static class ContourLabelGeometry
{
    public const string TextStyleName = "TCM_IZO_LABEL";

    public static ObjectId EnsureTextStyle(Transaction tr, Database db, string fontFileName)
    {
        var resolved = string.IsNullOrWhiteSpace(fontFileName)
            ? ContourPreferences.ContourLabelFont
            : fontFileName.Trim();
        resolved = StationFontCatalog.ResolveFileName(resolved);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = "arial.ttf";
        }

        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (table.Has(TextStyleName))
        {
            var existing = (TextStyleTableRecord)tr.GetObject(table[TextStyleName], OpenMode.ForWrite);
            if (!string.Equals(existing.FileName, resolved, StringComparison.OrdinalIgnoreCase))
            {
                existing.FileName = resolved;
            }

            return existing.ObjectId;
        }

        table.UpgradeOpen();
        var style = new TextStyleTableRecord
        {
            Name = TextStyleName,
            FileName = resolved
        };
        table.Add(style);
        tr.AddNewlyCreatedDBObject(style, true);
        return style.ObjectId;
    }

    public static string FormatElevation(double elevation) =>
        elevation.ToString("F" + ContourPreferences.ContourLabelDecimals,
            System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryGetPointAtDistance(Entity contour, double distanceAlong, out Point3d point, out double rotation)
    {
        point = Point3d.Origin;
        rotation = 0;
        if (contour is not Curve curve)
        {
            return false;
        }

        try
        {
            var length = GetCurveLength(curve);
            if (length < 1e-9)
            {
                return false;
            }

            var d = Math.Max(0, Math.Min(distanceAlong, length));
            point = curve.GetPointAtDist(d);
            var param = curve.GetParameterAtDistance(d);
            var deriv = curve.GetFirstDerivative(param);
            if (deriv.LengthSqrd > 1e-18)
            {
                rotation = Math.Atan2(deriv.Y, deriv.X);
            }

            // Elevacija izohipse (2D polyline).
            if (contour is Polyline pl)
            {
                point = new Point3d(point.X, point.Y, pl.Elevation);
            }
            else if (TerrainContourXData.TryReadRole(contour, out var role, out var elev) &&
                     role == TerrainContourXData.RoleContour)
            {
                point = new Point3d(point.X, point.Y, elev);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetDistanceAlong(Entity contour, Point3d nearPoint, out double distanceAlong, out Point3d onCurve, out double rotation)
    {
        distanceAlong = 0;
        onCurve = nearPoint;
        rotation = 0;
        if (contour is not Curve curve)
        {
            return false;
        }

        try
        {
            onCurve = curve.GetClosestPointTo(nearPoint, false);
            distanceAlong = curve.GetDistAtPoint(onCurve);
            var param = curve.GetParameterAtPoint(onCurve);
            var deriv = curve.GetFirstDerivative(param);
            if (deriv.LengthSqrd > 1e-18)
            {
                rotation = Math.Atan2(deriv.Y, deriv.X);
            }

            if (contour is Polyline pl)
            {
                onCurve = new Point3d(onCurve.X, onCurve.Y, pl.Elevation);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static double GetCurveLength(Curve curve)
    {
        try
        {
            if (curve is Polyline pl)
            {
                return pl.Length;
            }

            return curve.GetDistanceAtParameter(curve.EndParam);
        }
        catch
        {
            return 0;
        }
    }

    public static MText CreateLabel(
        Transaction tr,
        Database db,
        Point3d location,
        double rotation,
        double elevation,
        string? surfaceName = null)
    {
        ContourPreferences.Load();
        var styleId = EnsureTextStyle(tr, db, ContourPreferences.ContourLabelFont);
        var height = ContourPreferences.ContourLabelHeight;
        var layer = TerrainLayerNames.For(RoadCommands.ContourLabelLayer, surfaceName);
        EnsureLabelLayer(tr, db, layer);

        // Civil-like: MText na izohipsi. Maska linije = Wipeout (ne BackgroundFill).
        return new MText
        {
            Location = location,
            Contents = FormatElevation(elevation),
            TextHeight = height,
            Rotation = rotation,
            Attachment = AttachmentPoint.MiddleCenter,
            TextStyleId = styleId,
            Layer = layer,
            ColorIndex = ContourPreferences.ContourLabelColorAci
        };
    }

    /// <summary>
    /// Civil „Contour Line Only“ maska: Wipeout ispod teksta (prekida izohipsu vizuelno).
    /// Bez MText.BackgroundFill — taj API je uzrok eInvalidContext/FATAL.
    /// </summary>
    public static ObjectId CreateLineMaskWipeout(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        Point3d center,
        double rotation,
        double textHeight,
        string contents,
        string layer)
    {
        ContourPreferences.Load();
        if (!ContourPreferences.ContourLabelBackgroundMask)
        {
            return ObjectId.Null;
        }

        try
        {
            AcApp.SetSystemVariable("WIPEOUTFRAME", 0);
        }
        catch
        {
            // best-effort
        }

        var height = Math.Max(0.1, textHeight);
        contents ??= "";
        var width = Math.Max(height * 1.2, contents.Length * height * 0.55);
        var pad = 1.15;
        var halfW = width * 0.5 * pad;
        var halfH = height * 0.5 * pad;
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);

        Point3d Local(double x, double y) =>
            new(center.X + x * cos - y * sin, center.Y + x * sin + y * cos, center.Z);

        var corners = new Point2dCollection
        {
            To2d(Local(-halfW, -halfH)),
            To2d(Local(halfW, -halfH)),
            To2d(Local(halfW, halfH)),
            To2d(Local(-halfW, halfH)),
            To2d(Local(-halfW, -halfH))
        };

        var wipe = new Wipeout();
        wipe.SetDatabaseDefaults(db);
        wipe.SetFrom(corners, Vector3d.ZAxis);
        wipe.Layer = layer;
        modelSpace.AppendEntity(wipe);
        tr.AddNewlyCreatedDBObject(wipe, true);
        return wipe.ObjectId;
    }

    public static ObjectId CreateLineMaskWipeout(
        Transaction tr,
        Database db,
        BlockTableRecord modelSpace,
        MText label,
        string? surfaceName)
    {
        _ = surfaceName;
        return CreateLineMaskWipeout(
            tr, db, modelSpace,
            label.Location, label.Rotation, label.TextHeight, label.Contents ?? "", label.Layer);
    }

    private static Point2d To2d(Point3d p) => new(p.X, p.Y);

    private static void EnsureLabelLayer(Transaction tr, Database db, string layerName)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(layerName))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = layerName,
            Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 1)
        };
        layerTable.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    public static int SyncLabelsForContours(Transaction tr, Database db, IReadOnlyList<long> contourHandles)
    {
        if (contourHandles.Count == 0)
        {
            return 0;
        }

        var wanted = new HashSet<long>(contourHandles);
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        var updated = 0;
        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity label)
            {
                continue;
            }

            if (label is Face or Solid3d or PolyFaceMesh or SubDMesh or Wipeout)
            {
                continue;
            }

            if (!TerrainContourXData.TryReadContourLabel(
                    label, out _, out var elevation, out var parentHandle, out var distanceAlong,
                    out var surfaceName, out var wipeoutHandle) ||
                parentHandle == 0 ||
                !wanted.Contains(parentHandle))
            {
                continue;
            }

            ObjectId parentId;
            try
            {
                parentId = db.GetObjectId(false, new Handle(parentHandle), 0);
            }
            catch
            {
                continue;
            }

            if (parentId.IsNull || parentId.IsErased ||
                tr.GetObject(parentId, OpenMode.ForRead) is not Entity parent)
            {
                continue;
            }

            if (!TryGetPointAtDistance(parent, distanceAlong, out var point, out var rotation))
            {
                continue;
            }

            if (!label.IsWriteEnabled)
            {
                label.UpgradeOpen();
            }

            if (label is MText mtext)
            {
                mtext.Location = point;
                mtext.Rotation = rotation;
                mtext.Contents = FormatElevation(elevation);
                updated++;

                if (wipeoutHandle > 0)
                {
                    RelocateWipeout(
                        tr, db, wipeoutHandle, mtext,
                        elevation, parentHandle, distanceAlong, surfaceName);
                }
            }
            else if (label is DBText dbText)
            {
                dbText.Position = point;
                dbText.AlignmentPoint = point;
                dbText.Rotation = rotation;
                dbText.TextString = FormatElevation(elevation);
                updated++;
            }
        }

        return updated;
    }

    private static void RelocateWipeout(
        Transaction tr,
        Database db,
        long wipeoutHandle,
        MText label,
        double elevation,
        long parentContourHandle,
        double distanceAlong,
        string? surfaceName)
    {
        try
        {
            var wipeId = db.GetObjectId(false, new Handle(wipeoutHandle), 0);
            if (wipeId.IsNull || wipeId.IsErased ||
                tr.GetObject(wipeId, OpenMode.ForWrite) is not Wipeout wipe)
            {
                return;
            }

            var modelSpace = (BlockTableRecord)tr.GetObject(wipe.OwnerId, OpenMode.ForWrite);
            wipe.Erase();
            var newId = CreateLineMaskWipeout(
                tr, db, modelSpace,
                label.Location, label.Rotation, label.TextHeight, label.Contents ?? "", label.Layer);
            if (newId.IsNull)
            {
                return;
            }

            var newWipe = (Entity)tr.GetObject(newId, OpenMode.ForWrite);
            TerrainContourXData.AttachContourWipeout(newWipe, label.Handle.Value, elevation, surfaceName);
            if (!label.IsWriteEnabled)
            {
                label.UpgradeOpen();
            }

            TerrainContourXData.AttachContourLabel(
                label, elevation, parentContourHandle, distanceAlong, surfaceName, newWipe.Handle.Value);

            try
            {
                var drawOrder = (DrawOrderTable)tr.GetObject(modelSpace.DrawOrderTableId, OpenMode.ForWrite);
                drawOrder.MoveToTop(new ObjectIdCollection { newId });
                drawOrder.MoveToTop(new ObjectIdCollection { label.ObjectId });
            }
            catch
            {
                // ignore
            }
        }
        catch
        {
            // best-effort
        }
    }
}
