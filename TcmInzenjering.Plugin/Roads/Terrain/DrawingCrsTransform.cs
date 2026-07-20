namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// CRS crteža za podlogu — bez oslanjanja na AutoCAD geolokaciju.
/// </summary>
public enum BasemapDrawingCrs
{
    AutoGk = 0,
    Gk5 = 5,
    Gk6 = 6,
    Gk7 = 7,
    Utm34N = 34,
    AcadGeo = 100
}

/// <summary>
/// Direktna transformacija zvaničnih X/Y koordinata crteža u WGS84 lon/lat:
/// Gauss-Krüger (MGI 1901 / Bessel, zone 5–8, k0=0.9999, FE=zona*1e6+500000)
/// i UTM 34N (WGS84). Tačnost ~1 m — dovoljna za rastersku podlogu.
/// </summary>
internal static class DrawingCrsTransform
{
    // Bessel 1841
    private const double BesselA = 6377397.155;
    private const double BesselF = 1.0 / 299.1528128;

    // WGS84
    private const double Wgs84A = 6378137.0;
    private const double Wgs84F = 1.0 / 298.257223563;

    // MGI 1901 → WGS84 za Srbiju (EPSG:7676, tacnost 1 m, Position Vector konvencija).
    // Iste parametre koriste QGIS/PROJ i drugi geodetski programi; provereno
    // prema pyproj referenci (odstupanje < 1 m).
    private const double HDx = 577.88891;
    private const double HDy = 165.22205;
    private const double HDz = 391.18289;
    private const double HRxArcSec = 4.9145;
    private const double HRyArcSec = -0.94729;
    private const double HRzArcSec = -13.05098;
    private const double HScalePpm = 7.78664;

    /// <summary>Detekcija GK zone iz prefiksa easting-a (5.3–5.7M → 5, 6.3–6.7M → 6, …).</summary>
    public static bool TryDetectGkZone(double easting, out int zone)
    {
        zone = 0;
        var millions = (int)Math.Floor(easting / 1_000_000.0);
        if (millions is >= 5 and <= 8)
        {
            var offset = easting - millions * 1_000_000.0;
            if (offset is > 100_000 and < 900_000)
            {
                zone = millions;
                return true;
            }
        }

        return false;
    }

    public static bool TryToLonLat(
        BasemapDrawingCrs crs,
        double x,
        double y,
        out double lon,
        out double lat)
    {
        lon = 0;
        lat = 0;
        switch (crs)
        {
            case BasemapDrawingCrs.AutoGk:
                if (!TryDetectGkZone(x, out var zone))
                {
                    return false;
                }

                return TryGkToLonLat(zone, x, y, out lon, out lat);

            case BasemapDrawingCrs.Gk5:
            case BasemapDrawingCrs.Gk6:
            case BasemapDrawingCrs.Gk7:
                return TryGkToLonLat((int)crs, x, y, out lon, out lat);

            case BasemapDrawingCrs.Utm34N:
                InverseTransverseMercator(
                    Wgs84A, Wgs84F, 0.9996, DegToRad(21.0), 500_000, 0,
                    x, y, out lat, out lon);
                lon = RadToDeg(lon);
                lat = RadToDeg(lat);
                return IsValidLonLat(lon, lat);

            default:
                return false;
        }
    }

    public static bool TryGkToLonLat(int zone, double easting, double northing, out double lon, out double lat)
    {
        lon = 0;
        lat = 0;
        if (zone is < 5 or > 8)
        {
            return false;
        }

        var centralMeridian = DegToRad(zone * 3.0);
        var falseEasting = zone * 1_000_000.0 + 500_000.0;

        // 1) Inverzni TM na Bessel-u → MGI geodetske koordinate
        InverseTransverseMercator(
            BesselA, BesselF, 0.9999, centralMeridian, falseEasting, 0,
            easting, northing, out var latMgi, out var lonMgi);

        // 2) MGI (Bessel) → WGS84 preko ECEF + Helmert
        GeodeticToEcef(BesselA, BesselF, latMgi, lonMgi, 0, out var xb, out var yb, out var zb);
        HelmertPositionVector(xb, yb, zb, out var xw, out var yw, out var zw);
        EcefToGeodetic(Wgs84A, Wgs84F, xw, yw, zw, out var latW, out var lonW);

        lon = RadToDeg(lonW);
        lat = RadToDeg(latW);
        return IsValidLonLat(lon, lat);
    }

    public static string Describe(BasemapDrawingCrs crs) => crs switch
    {
        BasemapDrawingCrs.AutoGk => "Auto Gauss-Krüger (zona iz Y)",
        BasemapDrawingCrs.Gk5 => "Gauss-Krüger zona 5 (CM 15°)",
        BasemapDrawingCrs.Gk6 => "Gauss-Krüger zona 6 (CM 18°)",
        BasemapDrawingCrs.Gk7 => "Gauss-Krüger zona 7 (CM 21°)",
        BasemapDrawingCrs.Utm34N => "UTM 34N (WGS84)",
        _ => "AutoCAD geolokacija"
    };

    private static bool IsValidLonLat(double lon, double lat) =>
        lon is >= -180 and <= 180 && lat is >= -90 and <= 90 &&
        Math.Abs(lon) > 1e-9 && Math.Abs(lat) > 1e-9;

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;

    private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

    private static void InverseTransverseMercator(
        double a,
        double f,
        double k0,
        double lambda0,
        double falseEasting,
        double falseNorthing,
        double easting,
        double northing,
        out double lat,
        out double lon)
    {
        var e2 = f * (2 - f);
        var ep2 = e2 / (1 - e2);

        var m = (northing - falseNorthing) / k0;
        var mu = m / (a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));

        var sq = Math.Sqrt(1 - e2);
        var e1 = (1 - sq) / (1 + sq);

        var phi1 = mu +
                   (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu) +
                   (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu) +
                   151 * Math.Pow(e1, 3) / 96 * Math.Sin(6 * mu) +
                   1097 * Math.Pow(e1, 4) / 512 * Math.Sin(8 * mu);

        var sinPhi1 = Math.Sin(phi1);
        var cosPhi1 = Math.Cos(phi1);
        var tanPhi1 = Math.Tan(phi1);

        var c1 = ep2 * cosPhi1 * cosPhi1;
        var t1 = tanPhi1 * tanPhi1;
        var n1 = a / Math.Sqrt(1 - e2 * sinPhi1 * sinPhi1);
        var r1 = a * (1 - e2) / Math.Pow(1 - e2 * sinPhi1 * sinPhi1, 1.5);
        var d = (easting - falseEasting) / (n1 * k0);

        lat = phi1 - n1 * tanPhi1 / r1 *
            (d * d / 2 -
             (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24 +
             (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) *
             Math.Pow(d, 6) / 720);

        lon = lambda0 +
              (d -
               (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6 +
               (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) *
               Math.Pow(d, 5) / 120) / cosPhi1;
    }

    private static void GeodeticToEcef(
        double a,
        double f,
        double lat,
        double lon,
        double h,
        out double x,
        out double y,
        out double z)
    {
        var e2 = f * (2 - f);
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var n = a / Math.Sqrt(1 - e2 * sinLat * sinLat);
        x = (n + h) * cosLat * Math.Cos(lon);
        y = (n + h) * cosLat * Math.Sin(lon);
        z = (n * (1 - e2) + h) * sinLat;
    }

    private static void HelmertPositionVector(
        double x,
        double y,
        double z,
        out double xt,
        out double yt,
        out double zt)
    {
        const double arcSecToRad = Math.PI / (180.0 * 3600.0);
        var rx = HRxArcSec * arcSecToRad;
        var ry = HRyArcSec * arcSecToRad;
        var rz = HRzArcSec * arcSecToRad;
        var m = 1.0 + HScalePpm * 1e-6;

        xt = HDx + m * (x - rz * y + ry * z);
        yt = HDy + m * (rz * x + y - rx * z);
        zt = HDz + m * (-ry * x + rx * y + z);
    }

    private static void EcefToGeodetic(
        double a,
        double f,
        double x,
        double y,
        double z,
        out double lat,
        out double lon)
    {
        var e2 = f * (2 - f);
        lon = Math.Atan2(y, x);
        var p = Math.Sqrt(x * x + y * y);
        lat = Math.Atan2(z, p * (1 - e2));
        for (var i = 0; i < 8; i++)
        {
            var sinLat = Math.Sin(lat);
            var n = a / Math.Sqrt(1 - e2 * sinLat * sinLat);
            var h = p / Math.Cos(lat) - n;
            lat = Math.Atan2(z, p * (1 - e2 * n / (n + h)));
        }
    }
}
