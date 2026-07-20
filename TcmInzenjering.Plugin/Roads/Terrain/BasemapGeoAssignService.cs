using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Automatski postavlja geolokaciju crteža iz zvaničnih Gauss-Krüger X/Y koordinata:
/// detektuje zonu iz prefiksa Y (easting) koordinate, izračuna lon/lat referentne
/// tačke i dodeli odgovarajući Autodesk CRS kod (MGI-Balkans-N).
/// </summary>
internal static class BasemapGeoAssignService
{
    /// <summary>
    /// Vraća poruku o rezultatu. Ne baca izuzetke ka pozivaocu.
    /// </summary>
    public static string AssignFromDrawing(Database db)
    {
        try
        {
            if (!TryGetSamplePoint(db, out var sample))
            {
                return "Crtez nema sadrzaj (extents) — nacrtajte ili ucitajte tacke pa pokusajte ponovo.";
            }

            if (!DrawingCrsTransform.TryDetectGkZone(sample.X, out var zone))
            {
                return
                    $"Koordinate crteza (Y={sample.X:0}) ne lice na zvanicne Gauss-Krüger " +
                    "(ocekivan prefiks zone 5/6/7 u miliona metara). " +
                    "Za nezavisnu podlogu izaberite zonu rucno u polju 'CRS crteza'.";
            }

            if (!DrawingCrsTransform.TryGkToLonLat(zone, sample.X, sample.Y, out var lon, out var lat))
            {
                return "Ne mogu izracunati geografske koordinate iz X/Y — proverite koordinate crteza.";
            }

            // CRS se dodeljuje kao XML iz AutoCAD kataloga (goli kod baca eKeyNotFound).
            if (!TryFindCoordinateSystem(zone, out var csXml, out var csName))
            {
                return
                    $"AutoCAD katalog nema koordinatni sistem za Gauss-Krüger zonu {zone}. " +
                    "Nezavisna podloga (WMS/ArcGIS) radi i bez geolokacije — koristite " +
                    "'CRS crteza: Automatski'.";
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                GeoLocationData geo;
                var geoId = db.GeoDataObject;
                if (geoId.IsNull)
                {
                    geo = new GeoLocationData
                    {
                        BlockTableRecordId = db.CurrentSpaceId
                    };
                    geo.PostToDb();
                }
                else
                {
                    geo = (GeoLocationData)tr.GetObject(geoId, OpenMode.ForWrite);
                }

                // Redosled je bitan: prvo CRS, pa tek onda tacke (inace eKeyNotFound).
                geo.CoordinateSystem = csXml;
                geo.TypeOfCoordinates = TypeOfCoordinates.CoordinateTypeGrid;
                geo.HorizontalUnits = UnitsValue.Meters;
                geo.VerticalUnits = UnitsValue.Meters;
                geo.UpDirection = Vector3d.ZAxis;
                geo.NorthDirectionVector = new Vector2d(0, 1);
                geo.DesignPoint = new Point3d(sample.X, sample.Y, 0);
                geo.ReferencePoint = new Point3d(lon, lat, 0);
                tr.Commit();
            }

            var check = VerifyRoundTrip(db, sample, lon, lat);
            return
                $"Geolokacija postavljena automatski: Gauss-Krüger zona {zone} ({csName}), " +
                $"referentna tacka Y={sample.X:0.00} X={sample.Y:0.00} → " +
                $"lat {lat:0.000000}, lon {lon:0.000000}.{check}";
        }
        catch (System.Exception ex)
        {
            return "Automatska geolokacija nije uspela: " + ex.Message;
        }
    }

    /// <summary>
    /// Trazi Gauss-Krüger zonu u AutoCAD katalogu koordinatnih sistema:
    /// prvo po poznatim imenima, zatim po EPSG kodu kroz ceo katalog.
    /// Vraca XML reprezentaciju (jedini oblik koji GeoLocationData pouzdano prihvata).
    /// </summary>
    private static bool TryFindCoordinateSystem(int zone, out string csXml, out string csName)
    {
        csXml = string.Empty;
        csName = string.Empty;

        var nameCandidates = new[]
        {
            $"MGI1901.Balkans-{zone}/EN",
            $"MGI-Balkans-{zone}",
            $"MGI1901.Balkans-{zone}"
        };

        foreach (var candidate in nameCandidates)
        {
            try
            {
                using var cs = GeoCoordinateSystem.Create(candidate);
                csXml = cs.XmlRepresentation;
                csName = cs.ID;
                if (!string.IsNullOrWhiteSpace(csXml))
                {
                    return true;
                }
            }
            catch
            {
                // probaj sledeci kandidat
            }
        }

        // EPSG kodovi po prioritetu: srpske varijante (8677/8678/6316),
        // pa MGI 1901 / Balkans (3907–3910), pa stari deprecated (31275–31278).
        var epsgCandidates = zone switch
        {
            5 => new[] { 8677, 3907, 31275 },
            6 => new[] { 8678, 3908, 31276 },
            7 => new[] { 6316, 3909, 31277 },
            _ => new[] { 3910, 31278 }
        };

        var found = new Dictionary<int, (string Xml, string Name)>();
        try
        {
            foreach (var cs in GeoCoordinateSystem.CreateAll())
            {
                using (cs)
                {
                    var epsg = cs.EPSGcode;
                    if (Array.IndexOf(epsgCandidates, epsg) >= 0 && !found.ContainsKey(epsg))
                    {
                        found[epsg] = (cs.XmlRepresentation, cs.ID);
                    }
                }
            }
        }
        catch
        {
            // katalog nedostupan
        }

        foreach (var epsg in epsgCandidates)
        {
            if (found.TryGetValue(epsg, out var hit) && !string.IsNullOrWhiteSpace(hit.Xml))
            {
                csXml = hit.Xml;
                csName = $"{hit.Name} (EPSG:{epsg})";
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSamplePoint(Database db, out Point2d sample)
    {
        sample = default;
        try
        {
            var min = db.Extmin;
            var max = db.Extmax;
            if (max.X <= min.X || max.Y <= min.Y ||
                Math.Abs(min.X) > 1e12 || Math.Abs(max.X) > 1e12)
            {
                return false;
            }

            sample = new Point2d((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string VerifyRoundTrip(Database db, Point2d sample, double lon, double lat)
    {
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var geo = (GeoLocationData)tr.GetObject(db.GeoDataObject, OpenMode.ForRead);
            var ll = geo.TransformToLonLatAlt(new Point3d(sample.X, sample.Y, 0));
            tr.Commit();
            var dLon = Math.Abs(ll.X - lon);
            var dLat = Math.Abs(ll.Y - lat);
            if (dLon > 0.001 || dLat > 0.001)
            {
                return " UPOZORENJE: provera transformacije odstupa — proverite polozaj mape.";
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
