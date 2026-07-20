using System.IO;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal static class BasemapRasterAttachmentService
{
    public const string LayerName = "TCM_PODLOGA";
    private const string XDataApp = "TCM_PODLOGA";

    public static ObjectId Attach(
        Transaction tr,
        Database db,
        BasemapDownloadResult download,
        byte opacityPercent)
    {
        EnsureLayer(tr, db);
        EnsureRegApp(tr, db);

        var dictId = RasterImageDef.GetImageDictionary(db);
        if (dictId.IsNull)
        {
            dictId = RasterImageDef.CreateImageDictionary(db);
        }

        var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForWrite);
        var defName = MakeUniqueDefName(dict, Path.GetFileNameWithoutExtension(download.ImagePath));

        var imgDef = new RasterImageDef();
        imgDef.SourceFileName = download.ImagePath;
        imgDef.Load();
        var defId = dict.SetAt(defName, imgDef);
        tr.AddNewlyCreatedDBObject(imgDef, true);

        var origin = new Point3d(download.Min.X, download.Min.Y, 0);
        var width = download.Max.X - download.Min.X;
        var height = download.Max.Y - download.Min.Y;
        if (width <= 1e-9 || height <= 1e-9)
        {
            throw new InvalidOperationException("Neispravne dimenzije podloge.");
        }

        var image = new RasterImage
        {
            ImageDefId = defId,
            ShowImage = true,
            Layer = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByLayer, 256)
        };

        image.Orientation = new CoordinateSystem3d(
            origin,
            new Vector3d(width, 0, 0),
            new Vector3d(0, height, 0));

        // Fade: 0 = opaque, 100 = fully faded. Map opacity% → fade.
        var fade = (byte)Math.Max(0, Math.Min(90, 100 - opacityPercent));
        try
        {
            image.Fade = fade;
        }
        catch
        {
            // stariji hostovi mogu ignorisati
        }

        var ms = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        ms.AppendEntity(image);
        tr.AddNewlyCreatedDBObject(image, true);

        RasterImage.EnableReactors(true);
        image.AssociateRasterDef(imgDef);

        AttachXData(image, download, opacityPercent);
        SendToBack(tr, ms, image.ObjectId);
        AddAttributionText(tr, ms, download, origin, width, height);
        return image.ObjectId;
    }

    /// <summary>
    /// Mali tekst atribucije u donjem-levom uglu podloge (uslov korišćenja
    /// javnih map servisa — kao potpis izvora u Civil 3D).
    /// </summary>
    private static void AddAttributionText(
        Transaction tr,
        BlockTableRecord ms,
        BasemapDownloadResult download,
        Point3d origin,
        double width,
        double height)
    {
        var label = BuildAttributionLabel(download.SourceLabel);
        if (label.Length == 0)
        {
            return;
        }

        var textHeight = Math.Max(Math.Min(width, height) * 0.012, 1e-6);
        var margin = textHeight * 0.5;
        var text = new DBText
        {
            TextString = label,
            Position = new Point3d(origin.X + margin, origin.Y + margin, 0),
            Height = textHeight,
            Layer = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, 254)
        };
        ms.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }

    private static string BuildAttributionLabel(string sourceLabel)
    {
        var source = sourceLabel ?? string.Empty;
        if (source.IndexOf("arcgisonline", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("World_Imagery", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Izvor: Esri World Imagery — Esri, Maxar, Earthstar Geographics";
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return "Izvor: " + uri.Host;
        }

        // Lokalni fajl korisnika — atribucija nije potrebna.
        return string.Empty;
    }

    private static void AttachXData(
        RasterImage image,
        BasemapDownloadResult download,
        byte opacityPercent)
    {
        var values = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataApp),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, download.SourceLabel),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, download.Attribution),
            new TypedValue((int)DxfCode.ExtendedDataReal, download.Min.X),
            new TypedValue((int)DxfCode.ExtendedDataReal, download.Min.Y),
            new TypedValue((int)DxfCode.ExtendedDataReal, download.Max.X),
            new TypedValue((int)DxfCode.ExtendedDataReal, download.Max.Y),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)opacityPercent),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, DateTime.Now.ToString("o")));
        image.XData = values;
    }

    private static void SendToBack(Transaction tr, BlockTableRecord ms, ObjectId imageId)
    {
        try
        {
            if (ms.DrawOrderTableId.IsNull)
            {
                return;
            }

            var drawOrder = (DrawOrderTable)tr.GetObject(ms.DrawOrderTableId, OpenMode.ForWrite);
            using var ids = new ObjectIdCollection { imageId };
            drawOrder.MoveToBottom(ids);
        }
        catch
        {
            // draw order best-effort
        }
    }

    private static string MakeUniqueDefName(DBDictionary dict, string baseName)
    {
        var safe = string.Concat((baseName ?? "TCM_PODLOGA")
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "TCM_PODLOGA";
        }

        if (safe.Length > 28)
        {
            safe = safe[..28];
        }

        var name = safe;
        var i = 1;
        while (dict.Contains(name))
        {
            name = $"{safe}_{i++}";
        }

        return name;
    }

    private static void EnsureLayer(Transaction tr, Database db)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(LayerName))
        {
            return;
        }

        table.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, 8),
            IsOff = false,
            IsFrozen = false,
            IsLocked = false
        };
        table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static void EnsureRegApp(Transaction tr, Database db)
    {
        var table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (table.Has(XDataApp))
        {
            return;
        }

        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = XDataApp };
        table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }
}
