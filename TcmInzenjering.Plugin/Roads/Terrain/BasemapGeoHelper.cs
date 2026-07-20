using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal static class BasemapGeoHelper
{
    public static bool TryGetGeoData(
        Transaction tr,
        Database db,
        out GeoLocationData? geo,
        out string statusText)
    {
        geo = null;
        try
        {
            var id = db.GeoDataObject;
            if (id.IsNull)
            {
                statusText =
                    "Geolokacija nije postavljena. Kliknite 'Geolokacija iz X/Y (auto)' — " +
                    "izracunacu je iz zvanicnih Gauss-Krüger koordinata crteza. " +
                    "Nezavisna podloga (WMS/ArcGIS) radi i bez geolokacije.";
                return false;
            }

            geo = (GeoLocationData)tr.GetObject(id, OpenMode.ForRead);
            var cs = ExtractCsName(geo.CoordinateSystem ?? string.Empty);
            statusText = string.IsNullOrWhiteSpace(cs)
                ? "Geolokacija postoji, ali CRS kod nije procitan. Preporuceno: MAPCSASSIGN."
                : $"CRS / geolokacija: {cs}";
            return true;
        }
        catch (System.Exception ex)
        {
            statusText = $"Geolokacija nije dostupna: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// CoordinateSystem moze biti kratak kod ili cela XML definicija —
    /// iz XML-a vadi samo ime/opis sistema za prikaz korisniku.
    /// </summary>
    private static string ExtractCsName(string cs)
    {
        var trimmed = cs.Trim();
        if (!trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var desc = System.Text.RegularExpressions.Regex.Match(
            trimmed, "<Description>([^<]+)</Description>");
        var id = System.Text.RegularExpressions.Regex.Match(
            trimmed, "CoordinateSystem id=\"([^\"]+)\"");
        if (id.Success && desc.Success)
        {
            return $"{id.Groups[1].Value} ({desc.Groups[1].Value})";
        }

        if (id.Success)
        {
            return id.Groups[1].Value;
        }

        return desc.Success ? desc.Groups[1].Value : "definisana (XML)";
    }

    public static string DescribeGeoStatus(Database db)
    {
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            TryGetGeoData(tr, db, out _, out var status);
            tr.Commit();
            return status;
        }
        catch (System.Exception ex)
        {
            return $"Geolokacija nije dostupna: {ex.Message}";
        }
    }

    /// <summary>
    /// DWG ugao → (lon, lat) preko GeoLocationData. X=lon, Y=lat.
    /// </summary>
    public static bool TryToLonLat(
        GeoLocationData geo,
        Point3d dwgPoint,
        out double lon,
        out double lat)
    {
        lon = 0;
        lat = 0;
        try
        {
            var ll = geo.TransformToLonLatAlt(dwgPoint);
            lon = ll.X;
            lat = ll.Y;
            return lon is >= -180 and <= 180 && lat is >= -90 and <= 90;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryBoundsToLonLat(
        GeoLocationData geo,
        Point2d min,
        Point2d max,
        out double minLon,
        out double minLat,
        out double maxLon,
        out double maxLat)
    {
        minLon = minLat = maxLon = maxLat = 0;
        var corners = new[]
        {
            new Point3d(min.X, min.Y, 0),
            new Point3d(max.X, min.Y, 0),
            new Point3d(max.X, max.Y, 0),
            new Point3d(min.X, max.Y, 0)
        };

        var lons = new List<double>(4);
        var lats = new List<double>(4);
        foreach (var c in corners)
        {
            if (!TryToLonLat(geo, c, out var lon, out var lat))
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

    public static void LonLatToWebMercator(
        double lon,
        double lat,
        out double x,
        out double y)
    {
        const double originShift = 20037508.342789244;
        lon = Math.Max(-180, Math.Min(180, lon));
        lat = Math.Max(-85.05112878, Math.Min(85.05112878, lat));
        x = lon * originShift / 180.0;
        var rad = lat * Math.PI / 180.0;
        y = Math.Log(Math.Tan(Math.PI / 4.0 + rad / 2.0)) * originShift / Math.PI;
    }

    public static void BoundsToWebMercator(
        double minLon,
        double minLat,
        double maxLon,
        double maxLat,
        out double minX,
        out double minY,
        out double maxX,
        out double maxY)
    {
        LonLatToWebMercator(minLon, minLat, out minX, out minY);
        LonLatToWebMercator(maxLon, maxLat, out maxX, out maxY);
        if (minX > maxX)
        {
            (minX, maxX) = (maxX, minX);
        }

        if (minY > maxY)
        {
            (minY, maxY) = (maxY, minY);
        }
    }
}
