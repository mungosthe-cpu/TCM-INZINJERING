using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Privremeno prikazuje imenovani skup tačaka: crveni krstovi + debela granica skupa.
/// Jedan klik u crtežu zatvara preview i vraća Project prozor.
/// </summary>
internal static class TerrainPointPreview
{
    private const int MaxCrosses = 2000;

    public static bool Show(string surfaceName, out string? error)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            error = "Nema aktivnog crteza.";
            return false;
        }

        IReadOnlyList<Point3d>? points;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            points = NamedTerrainSurfaceStore.TryLoadSurface(tr, doc.Database, surfaceName);
            tr.Commit();
        }

        if (points is null || points.Count == 0)
        {
            error = $"Teren „{surfaceName}“ nema sacuvane tacke.";
            return false;
        }

        return ShowPoints(points, $"„{surfaceName}“", out error);
    }

    /// <summary>Privremeni prikaz proizvoljnog skupa tačaka (krstovi + granica + zoom).</summary>
    public static bool ShowPoints(
        IReadOnlyList<Point3d> points,
        string label,
        out string? error)
    {
        error = null;
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            error = "Nema aktivnog crteza.";
            return false;
        }

        if (points.Count == 0)
        {
            error = "Nema tacaka za prikaz.";
            return false;
        }

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        var diagonal = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
        var crossSize = Math.Max(diagonal * 0.0025, 0.25);

        var transients = BuildTransients(points, crossSize);
        var manager = TransientManager.CurrentTransientManager;
        var viewports = new IntegerCollection();

        try
        {
            // Prvo zoom, pa tek onda transient grafika — bez punog Regen-a,
            // koji bi ostavio kratak crni fleš dok se crtez ponovo iscrtava.
            ZoomToPoints(doc.Editor, points, 0.08);

            foreach (var drawable in transients)
            {
                manager.AddTransient(
                    drawable,
                    TransientDrawingMode.DirectShortTerm,
                    128,
                    viewports);
            }

            doc.Editor.UpdateScreen();

            var prompt = new PromptPointOptions(
                $"\nTCM-ROADS: {label} — {points.Count} tacaka. " +
                "Crveni krstovi su tacke, debela linija je granica. Kliknite za povratak.")
            {
                AllowNone = true
            };
            doc.Editor.GetPoint(prompt);
            return true;
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            foreach (var drawable in transients)
            {
                try
                {
                    manager.EraseTransient(drawable, viewports);
                }
                catch
                {
                    // Preview je best-effort.
                }

                drawable.Dispose();
            }

            doc.Editor.UpdateScreen();
        }
    }

    private static List<Entity> BuildTransients(IReadOnlyList<Point3d> points, double crossSize)
    {
        var entities = new List<Entity>();
        var red = Color.FromColorIndex(ColorMethod.ByAci, 1);

        // Za velike skupove ravnomerno uzorkuj prikaz; granica uvek koristi sve tačke.
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / (double)MaxCrosses));
        for (var i = 0; i < points.Count; i += step)
        {
            var p = points[i];
            entities.Add(new Line(
                new Point3d(p.X - crossSize, p.Y, p.Z),
                new Point3d(p.X + crossSize, p.Y, p.Z))
            {
                Color = red,
                LineWeight = LineWeight.LineWeight050
            });
            entities.Add(new Line(
                new Point3d(p.X, p.Y - crossSize, p.Z),
                new Point3d(p.X, p.Y + crossSize, p.Z))
            {
                Color = red,
                LineWeight = LineWeight.LineWeight050
            });
        }

        var hull = ConvexHull(points);
        if (hull.Count >= 3)
        {
            var boundary = new AcPolyline(hull.Count)
            {
                Closed = true,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 2),
                ConstantWidth = Math.Max(crossSize * 0.28, 0.08),
                LineWeight = LineWeight.LineWeight100
            };
            for (var i = 0; i < hull.Count; i++)
            {
                boundary.AddVertexAt(i, hull[i], 0, 0, 0);
            }

            entities.Add(boundary);
        }

        return entities;
    }

    private static List<Point2d> ConvexHull(IReadOnlyList<Point3d> points)
    {
        var pts = points
            .Select(p => new Point2d(p.X, p.Y))
            .Distinct(new Point2dComparer())
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();
        if (pts.Count <= 2)
        {
            return pts;
        }

        static double Cross(Point2d o, Point2d a, Point2d b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var lower = new List<Point2d>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 &&
                   Cross(lower[^2], lower[^1], p) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(p);
        }

        var upper = new List<Point2d>();
        for (var i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 &&
                   Cross(upper[^2], upper[^1], p) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static void ZoomToPoints(Editor ed, IReadOnlyList<Point3d> points, double marginRatio)
    {
        var min = new Point3d(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        var max = new Point3d(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
        var ext = new Extents3d(min, max);

        using var view = ed.GetCurrentView();
        var worldToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
        worldToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * worldToDcs;
        worldToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * worldToDcs;
        ext.TransformBy(worldToDcs.Inverse());

        var width = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, 1.0);
        var height = Math.Max(ext.MaxPoint.Y - ext.MinPoint.Y, 1.0);
        var margin = Math.Max(0, marginRatio);
        view.Width = width * (1 + 2 * margin);
        view.Height = height * (1 + 2 * margin);
        view.CenterPoint = new Point2d(
            (ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
            (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5);
        ed.SetCurrentView(view);
    }

    private sealed class Point2dComparer : IEqualityComparer<Point2d>
    {
        public bool Equals(Point2d a, Point2d b) =>
            Math.Abs(a.X - b.X) <= 1e-8 &&
            Math.Abs(a.Y - b.Y) <= 1e-8;

        public int GetHashCode(Point2d p)
        {
            unchecked
            {
                var hx = Math.Round(p.X, 8).GetHashCode();
                var hy = Math.Round(p.Y, 8).GetHashCode();
                return (hx * 397) ^ hy;
            }
        }
    }
}
