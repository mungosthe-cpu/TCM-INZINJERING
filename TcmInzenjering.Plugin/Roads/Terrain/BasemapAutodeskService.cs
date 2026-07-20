using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Dialogs;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Pokreće ugrađene Autodesk GEOMAP / GEOMAPIMAGE komande (Esri, opciono Bing).
/// </summary>
internal static class BasemapAutodeskService
{
    public static void Run(Editor ed, BasemapSettings settings, Point2d? areaMin, Point2d? areaMax)
    {
        if (string.Equals(settings.MapStyleTag, "Off", StringComparison.OrdinalIgnoreCase))
        {
            TryCommand(ed, "_.GEOMAP", "_Off");
            ed.WriteMessage("\nTCM-ROADS: Online mapa iskljucena.");
            return;
        }

        // Za capture: prvo ukljuci stil, pa ugradi.
        ApplyMapStyle(ed, settings.MapStyleTag);

        switch (settings.AutodeskAction)
        {
            case BasemapAutodeskAction.Live:
                ed.WriteMessage(
                    "\nTCM-ROADS: Online mapa prikazana u viewportu (GEOMAP). " +
                    "Za stampu/offline koristite ugradjivanje oblasti.");
                break;

            case BasemapAutodeskAction.CaptureViewport:
                if (!TryCommand(ed, "_.GEOMAPIMAGE", "_V") &&
                    !TryCommand(ed, "_.GEOMAPIMAGE", "_Viewport"))
                {
                    throw new InvalidOperationException(
                        "GEOMAPIMAGE nije uspeo. Proverite Autodesk prijavu, " +
                        "plan pogled WCS i geolokaciju crteza.");
                }

                ed.WriteMessage("\nTCM-ROADS: Viewport mape ugradjen u crtez (GEOMAPIMAGE).");
                break;

            case BasemapAutodeskAction.CaptureArea:
                if (areaMin is null || areaMax is null)
                {
                    throw new InvalidOperationException("Oblast za capture nije definisana.");
                }

                var p1 = new Point3d(areaMin.Value.X, areaMin.Value.Y, 0);
                var p2 = new Point3d(areaMax.Value.X, areaMax.Value.Y, 0);
                if (!TryCommand(ed, "_.GEOMAPIMAGE", p1, p2))
                {
                    throw new InvalidOperationException(
                        "GEOMAPIMAGE (oblast) nije uspeo. Proverite Autodesk prijavu i plan pogled WCS.");
                }

                ed.WriteMessage("\nTCM-ROADS: Oblast mape ugradjena u crtez (GEOMAPIMAGE).");
                break;
        }
    }

    private static void ApplyMapStyle(Editor ed, string styleTag)
    {
        // 2025/2026: Esri prvi. Starije verzije: Bing Aerial/Hybrid.
        var attempts = styleTag switch
        {
            "EsriImagery" => new[]
            {
                new object[] { "_.GEOMAP", "_Esri", "_Imagery" },
                new object[] { "_.GEOMAP", "_A" },
                new object[] { "_.GEOMAP", "_Aerial" }
            },
            "EsriStreets" => new[]
            {
                new object[] { "_.GEOMAP", "_Esri", "_Streets" },
                new object[] { "_.GEOMAP", "_R" },
                new object[] { "_.GEOMAP", "_Road" }
            },
            "EsriOsm" => new[]
            {
                new object[] { "_.GEOMAP", "_Esri", "_OpenStreetMap" },
                new object[] { "_.GEOMAP", "_Esri", "_OSM" }
            },
            "BingAerial" => new[]
            {
                new object[] { "_.GEOMAP", "_Bing", "_Aerial" },
                new object[] { "_.GEOMAP", "_A" },
                new object[] { "_.GEOMAP", "_Aerial" }
            },
            "BingHybrid" => new[]
            {
                new object[] { "_.GEOMAP", "_Bing", "_Hybrid" },
                new object[] { "_.GEOMAP", "_H" },
                new object[] { "_.GEOMAP", "_Hybrid" }
            },
            _ => new[]
            {
                new object[] { "_.GEOMAP", "_Esri", "_Imagery" },
                new object[] { "_.GEOMAP", "_A" }
            }
        };

        foreach (var args in attempts)
        {
            if (TryCommand(ed, args))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "GEOMAP nije uspeo. Prijavite se na Autodesk nalog, dodelite geolokaciju " +
            "i postavite plan pogled World UCS.");
    }

    private static bool TryCommand(Editor ed, params object[] args)
    {
        try
        {
            ed.Command(args);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
