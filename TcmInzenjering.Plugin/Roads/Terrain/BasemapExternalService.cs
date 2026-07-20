using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Dialogs;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal sealed class BasemapDownloadResult
{
    public string ImagePath { get; init; } = string.Empty;
    public Point2d Min { get; init; }
    public Point2d Max { get; init; }
    public string SourceLabel { get; init; } = string.Empty;
    public string Attribution { get; init; } = string.Empty;
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
}

/// <summary>
/// Preuzima WMS/ArcGIS sliku ili priprema lokalni georeferencirani raster.
/// </summary>
internal static class BasemapExternalService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    static BasemapExternalService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("TCM-ROADS-Basemap/1.0");
    }

    public static BasemapDownloadResult Prepare(
        Database db,
        BasemapSettings settings,
        Point2d min,
        Point2d max,
        IProgress<(int Percent, string Status)>? progress = null)
    {
        if (settings.ExternalSource == BasemapExternalSource.LocalFile)
        {
            progress?.Report((20, "Citam lokalni raster…"));
            return PrepareLocal(settings.LocalFilePath, min, max);
        }

        progress?.Report((10, "Racunam geografske koordinate oblasti…"));
        if (!TryBoundsToLonLat(
                db, settings.DrawingCrs, min, max,
                out var minLon, out var minLat, out var maxLon, out var maxLat))
        {
            throw new InvalidOperationException(
                settings.DrawingCrs == BasemapDrawingCrs.AcadGeo
                    ? "Ne mogu pretvoriti oblast crteza u geografske koordinate preko AutoCAD geolokacije. " +
                      "Izaberite 'Automatski — Gauss-Krüger' u polju CRS crteza."
                    : "Ne mogu pretvoriti oblast crteza u geografske koordinate. " +
                      "Proverite da li su koordinate crteza zvanicne Gauss-Krüger (Y sa prefiksom zone, " +
                      "npr. 6 604 600 / 4 975 000) ili rucno izaberite zonu u polju CRS crteza.");
        }

        // Sanity provera pre slanja zahteva: nerealna geografska oblast znaci da
        // koordinate crteza nisu u izabranom CRS-u (server bi vratio gresku 500).
        if (double.IsNaN(minLon) || double.IsNaN(minLat) ||
            maxLon - minLon < 1e-7 || maxLat - minLat < 1e-7 ||
            minLat < -85 || maxLat > 85)
        {
            throw new InvalidOperationException(
                $"Oblast crteza se preslikala u nevazecu geografsku oblast " +
                $"(lon {minLon:F6}…{maxLon:F6}, lat {minLat:F6}…{maxLat:F6}). " +
                $"Koordinate ovog crteza verovatno nisu u izabranom CRS-u " +
                $"({DrawingCrsTransform.Describe(settings.DrawingCrs)}) — proverite da li " +
                "crtez koristi zvanicne Gauss-Krüger koordinate (Y sa prefiksom zone, npr. " +
                "6 604 600 / 4 975 000) ili izaberite drugu zonu u polju 'CRS crteza'.");
        }

        progress?.Report((15,
            $"Oblast: lon {minLon:F5}…{maxLon:F5}, lat {minLat:F5}…{maxLat:F5}"));

        BasemapGeoHelper.BoundsToWebMercator(
            minLon, minLat, maxLon, maxLat,
            out var minX, out var minY, out var maxX, out var maxY);

        if (maxX - minX < 0.5 || maxY - minY < 0.5)
        {
            throw new InvalidOperationException(
                "Oznacena oblast je premala ili degenerisana posle transformacije — " +
                "oznacite vecu oblast u crtezu.");
        }

        // Esri/ArcGIS serveri ogranicavaju izlaz na 4096×4096 px ("Error: bytes"
        // za vece) — uklopi obe dimenzije u limit uz ocuvanje razmere oblasti.
        ComputeImageSize(minX, minY, maxX, maxY, settings.ResolutionPx,
            out var width, out var height);
        var outDir = Path.Combine(ProjectFolderPreferences.EnsureFolder(), "Basemap");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(
            outDir,
            $"tcm_podloga_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        progress?.Report((35, "Preuzimam satelitsku sliku…"));
        var url = settings.ExternalSource == BasemapExternalSource.ArcGisWorld
            ? BasemapPreferences.DefaultArcGisWorldUrl
            : settings.ServiceUrl;

        if (IsArcGisMapServer(url))
        {
            DownloadArcGisExport(url, minX, minY, maxX, maxY, width, height, outPath, progress);
        }
        else
        {
            DownloadWmsGetMap(
                url, settings.WmsLayer, minX, minY, maxX, maxY, width, height, outPath, progress);
        }

        progress?.Report((85, "Snimam world-file…"));
        WriteWorldFile(outPath, min, max, width, height);
        GetImageSize(outPath, out width, out height);

        return new BasemapDownloadResult
        {
            ImagePath = outPath,
            Min = min,
            Max = max,
            SourceLabel = url,
            Attribution = "Esri / izvor servisa — proverite uslove koriscenja i atribuciju.",
            PixelWidth = width,
            PixelHeight = height
        };
    }

    /// <summary>
    /// Uglove oblasti pretvara u lon/lat: direktno iz zvanicnih X/Y koordinata
    /// (Gauss-Krüger / UTM), ili preko AutoCAD geolokacije ako je tako izabrano.
    /// </summary>
    private static bool TryBoundsToLonLat(
        Database db,
        BasemapDrawingCrs crs,
        Point2d min,
        Point2d max,
        out double minLon,
        out double minLat,
        out double maxLon,
        out double maxLat)
    {
        minLon = minLat = maxLon = maxLat = 0;

        if (crs == BasemapDrawingCrs.AcadGeo)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var ok = BasemapGeoHelper.TryGetGeoData(tr, db, out var geo, out _) &&
                     geo is not null &&
                     BasemapGeoHelper.TryBoundsToLonLat(
                         geo, min, max, out minLon, out minLat, out maxLon, out maxLat);
            tr.Commit();
            return ok;
        }

        var corners = new[]
        {
            (min.X, min.Y),
            (max.X, min.Y),
            (max.X, max.Y),
            (min.X, max.Y)
        };

        var lons = new List<double>(4);
        var lats = new List<double>(4);
        foreach (var (x, y) in corners)
        {
            if (!DrawingCrsTransform.TryToLonLat(crs, x, y, out var lon, out var lat))
            {
                return false;
            }

            lons.Add(lon);
            lats.Add(lat);
        }

        minLon = lons.Min();
        maxLon = lons.Max();
        minLat = lats.Min();
        maxLat = lats.Max();
        return maxLon > minLon && maxLat > minLat;
    }

    private static BasemapDownloadResult PrepareLocal(string path, Point2d pickMin, Point2d pickMax)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Lokalni raster nije pronadjen.", path);
        }

        GetImageSize(path, out var width, out var height);
        if (!TryReadWorldFile(path, width, height, out var min, out var max))
        {
            // Bez world-file: koristi oznacenu oblast u crtezu.
            min = pickMin;
            max = pickMax;
            if (max.X - min.X < 1e-6 || max.Y - min.Y < 1e-6)
            {
                throw new InvalidOperationException(
                    "Lokalni fajl nema world-file (.tfw/.jgw/.pgw). " +
                    "Oznacite oblast u crtezu gde treba da se smesti, ili dodajte world-file.");
            }
        }

        return new BasemapDownloadResult
        {
            ImagePath = path,
            Min = min,
            Max = max,
            SourceLabel = path,
            Attribution = "Lokalni raster korisnika.",
            PixelWidth = width,
            PixelHeight = height
        };
    }

    private static void DownloadArcGisExport(
        string mapServerUrl,
        double minX,
        double minY,
        double maxX,
        double maxY,
        int width,
        int height,
        string outPath,
        IProgress<(int Percent, string Status)>? progress)
    {
        var baseUrl = mapServerUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/export", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/export".Length];
        }

        var inv = CultureInfo.InvariantCulture;

        // Esri export servis povremeno vraca "Error: bytes" (500) pod opterecenjem
        // (narocito 16–18h CET); identican zahtev obicno prodje iz 2.-3. pokusaja.
        // Pokusaji: 2× puna rezolucija, pa 2× umanjena (manji zahtev = manji teret).
        // Kratke pauze — dugacke pauze + spori odgovori su ranije davali utisak
        // da je komanda "zamrznuta".
        var attempts = new[]
        {
            (W: width, H: height, DelayMs: 0),
            (W: width, H: height, DelayMs: 1000),
            (W: Math.Max(256, width / 2), H: Math.Max(256, height / 2), DelayMs: 1500),
            (W: Math.Max(256, width / 4), H: Math.Max(256, height / 4), DelayMs: 2000)
        };

        System.Exception? lastError = null;
        for (var i = 0; i < attempts.Length; i++)
        {
            var (w, h, delayMs) = attempts[i];
            if (delayMs > 0)
            {
                System.Threading.Thread.Sleep(delayMs);
            }

            var query =
                $"{baseUrl}/export?" +
                $"bbox={minX.ToString(inv)},{minY.ToString(inv)},{maxX.ToString(inv)},{maxY.ToString(inv)}" +
                "&bboxSR=3857&imageSR=3857" +
                $"&size={w},{h}" +
                "&format=png&transparent=false&f=image";

            progress?.Report((50,
                i == 0
                    ? "ArcGIS export…"
                    : $"ArcGIS export — pokusaj {i + 1}/{attempts.Length} ({w}×{h} px)…"));
            try
            {
                // 60 s po pokusaju: preopterecen Esri servis ume da "visi" do
                // globalnog timeout-a (3 min) — brze je preci na manji zahtev.
                DownloadToFile(query, outPath, TimeSpan.FromSeconds(60));
                return;
            }
            catch (System.Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            (lastError?.Message ?? "Preuzimanje nije uspelo.") +
            " Esri servis je trenutno preopterecen (cesto 16–18h) — pokusajte ponovo " +
            "za par minuta ili izaberite manju rezoluciju.");
    }

    private static void DownloadWmsGetMap(
        string wmsUrl,
        string layer,
        double minX,
        double minY,
        double maxX,
        double maxY,
        int width,
        int height,
        string outPath,
        IProgress<(int Percent, string Status)>? progress)
    {
        var inv = CultureInfo.InvariantCulture;
        var layers = string.IsNullOrWhiteSpace(layer) ? "0" : layer.Trim();
        var sep = wmsUrl.Contains('?') ? "&" : "?";
        var query =
            $"{wmsUrl.Trim()}{sep}" +
            "SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
            $"&LAYERS={Uri.EscapeDataString(layers)}" +
            "&CRS=EPSG:3857" +
            $"&BBOX={minX.ToString(inv)},{minY.ToString(inv)},{maxX.ToString(inv)},{maxY.ToString(inv)}" +
            $"&WIDTH={width}&HEIGHT={height}" +
            "&FORMAT=image/png&STYLES=&TRANSPARENT=FALSE";

        progress?.Report((50, "WMS GetMap…"));
        try
        {
            DownloadToFile(query, outPath);
        }
        catch
        {
            // WMS 1.1.1 često koristi SRS i BBOX kao minx,miny,maxx,maxy u lon/lat ili projected.
            var fallback =
                $"{wmsUrl.Trim()}{sep}" +
                "SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap" +
                $"&LAYERS={Uri.EscapeDataString(layers)}" +
                "&SRS=EPSG:3857" +
                $"&BBOX={minX.ToString(inv)},{minY.ToString(inv)},{maxX.ToString(inv)},{maxY.ToString(inv)}" +
                $"&WIDTH={width}&HEIGHT={height}" +
                "&FORMAT=image/png&STYLES=&TRANSPARENT=FALSE";
            DownloadToFile(fallback, outPath);
        }
    }

    private static void DownloadToFile(string url, string outPath, TimeSpan? timeout = null)
    {
        using var cts = new System.Threading.CancellationTokenSource(
            timeout ?? TimeSpan.FromMinutes(3));
        HttpResponseMessage response;
        try
        {
            response = Http.GetAsync(url, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Servis nije odgovorio u zadatom roku (timeout).");
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var detail = string.Empty;
            try
            {
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    detail = " Odgovor servera: " +
                             body.Substring(0, Math.Min(body.Length, 240));
                }
            }
            catch
            {
                // bez detalja
            }

            throw new InvalidOperationException(
                $"Preuzimanje nije uspelo ({(int)response.StatusCode}). " +
                "Proverite URL, mrezu i uslove servisa." + detail);
        }

        var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.Length < 100)
        {
            throw new InvalidOperationException("Servis je vratio prazan odgovor.");
        }

        if (media.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0 ||
            media.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var preview = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 240));
            throw new InvalidOperationException("Servis nije vratio sliku: " + preview);
        }

        File.WriteAllBytes(outPath, bytes);
    }

    private static bool IsArcGisMapServer(string url) =>
        url.IndexOf("/MapServer", StringComparison.OrdinalIgnoreCase) >= 0 ||
        url.IndexOf("/ImageServer", StringComparison.OrdinalIgnoreCase) >= 0;

    private static void ComputeImageSize(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int requestedWidth,
        out int width,
        out int height)
    {
        const int maxDim = 4096;
        const int minDim = 256;

        var w = Math.Max(maxX - minX, 1e-6);
        var h = Math.Max(maxY - minY, 1e-6);
        var aspect = h / w;

        width = Math.Max(minDim, Math.Min(maxDim, requestedWidth));
        height = (int)Math.Round(width * aspect);
        if (height > maxDim)
        {
            height = maxDim;
            width = Math.Max(minDim, (int)Math.Round(maxDim / aspect));
        }

        height = Math.Max(minDim, height);
    }

    private static void WriteWorldFile(
        string imagePath,
        Point2d min,
        Point2d max,
        int width,
        int height)
    {
        if (width < 1 || height < 1)
        {
            return;
        }

        var pixelW = (max.X - min.X) / width;
        var pixelH = (max.Y - min.Y) / height;
        // World file: gornji-levi centar piksela.
        var lines = string.Join(
            Environment.NewLine,
            pixelW.ToString("R", CultureInfo.InvariantCulture),
            "0",
            "0",
            (-pixelH).ToString("R", CultureInfo.InvariantCulture),
            (min.X + pixelW * 0.5).ToString("R", CultureInfo.InvariantCulture),
            (max.Y - pixelH * 0.5).ToString("R", CultureInfo.InvariantCulture));

        var wf = Path.ChangeExtension(imagePath, null) + GetWorldFileExtension(imagePath);
        File.WriteAllText(wf, lines + Environment.NewLine);
    }

    private static string GetWorldFileExtension(string imagePath)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => ".jgw",
            ".png" => ".pgw",
            ".tif" or ".tiff" => ".tfw",
            ".bmp" => ".bpw",
            _ => ".wld"
        };
    }

    private static bool TryReadWorldFile(
        string imagePath,
        int width,
        int height,
        out Point2d min,
        out Point2d max)
    {
        min = default;
        max = default;
        var candidates = new[]
        {
            Path.ChangeExtension(imagePath, null) + GetWorldFileExtension(imagePath),
            Path.ChangeExtension(imagePath, ".wld"),
            Path.ChangeExtension(imagePath, ".tfw")
        };

        foreach (var wf in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(wf))
            {
                continue;
            }

            try
            {
                var lines = File.ReadAllLines(wf)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToArray();
                if (lines.Length < 6)
                {
                    continue;
                }

                var a = double.Parse(lines[0], CultureInfo.InvariantCulture);
                var d = double.Parse(lines[1], CultureInfo.InvariantCulture);
                var b = double.Parse(lines[2], CultureInfo.InvariantCulture);
                var e = double.Parse(lines[3], CultureInfo.InvariantCulture);
                var c = double.Parse(lines[4], CultureInfo.InvariantCulture);
                var f = double.Parse(lines[5], CultureInfo.InvariantCulture);

                // Centar gornjeg-levog piksela → ivice.
                var ulx = c - a * 0.5 - b * 0.5;
                var uly = f - d * 0.5 - e * 0.5;
                var lrx = ulx + a * width + b * height;
                var lry = uly + d * width + e * height;
                min = new Point2d(Math.Min(ulx, lrx), Math.Min(uly, lry));
                max = new Point2d(Math.Max(ulx, lrx), Math.Max(uly, lry));
                return max.X > min.X && max.Y > min.Y;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    private static void GetImageSize(string path, out int width, out int height)
    {
        using var img = System.Drawing.Image.FromFile(path);
        width = img.Width;
        height = img.Height;
    }
}
