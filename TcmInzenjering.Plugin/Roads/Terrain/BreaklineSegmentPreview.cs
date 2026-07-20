using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Privremeno prikazuje neuspele breakline segmente debelom crvenom linijom.</summary>
internal static class BreaklineSegmentPreview
{
    public static bool Show(
        IReadOnlyList<(Point3d A, Point3d B)> segments,
        out string? error)
    {
        error = null;
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            error = "Nema aktivnog crteza.";
            return false;
        }

        if (segments.Count == 0)
        {
            error = "Nema neuspelih segmenata za prikaz.";
            return false;
        }

        var drawables = segments.Select(s => (Entity)new Line(s.A, s.B)
        {
            Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
            LineWeight = LineWeight.LineWeight200
        }).ToList();
        var manager = TransientManager.CurrentTransientManager;
        var viewports = new IntegerCollection();

        try
        {
            Zoom(doc.Editor, segments);
            foreach (var drawable in drawables)
            {
                manager.AddTransient(
                    drawable,
                    TransientDrawingMode.DirectShortTerm,
                    129,
                    viewports);
            }

            doc.Editor.UpdateScreen();
            doc.Editor.GetPoint(new PromptPointOptions(
                $"\nTCM-ROADS: {segments.Count} neuspelih segmenata prikazano je debelom crvenom linijom. " +
                "Kliknite za povratak.")
            {
                AllowNone = true
            });
            return true;
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            foreach (var drawable in drawables)
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

    private static void Zoom(
        Editor editor,
        IReadOnlyList<(Point3d A, Point3d B)> segments)
    {
        var points = segments.SelectMany(s => new[] { s.A, s.B }).ToList();
        var extents = new Extents3d(
            new Point3d(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
            new Point3d(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));

        using var view = editor.GetCurrentView();
        var worldToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
        worldToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * worldToDcs;
        worldToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * worldToDcs;
        extents.TransformBy(worldToDcs.Inverse());

        var width = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, 1.0);
        var height = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, 1.0);
        view.Width = width * 1.2;
        view.Height = height * 1.2;
        view.CenterPoint = new Point2d(
            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
        editor.SetCurrentView(view);
    }
}
