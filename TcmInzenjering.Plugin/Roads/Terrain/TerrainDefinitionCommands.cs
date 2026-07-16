using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Roads;

public sealed partial class RoadCommands
{
    /// <summary>Dodaje breakline polilinije u definiciju terena (obavezne TIN ivice).</summary>
    [CommandMethod("TCMTERBREAK", CommandFlags.Modal)]
    public void AddTerrainBreaklines()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        try
        {
            var opts = new PromptSelectionOptions
            {
                MessageForAdding = "\nIzaberite breakline polilinije (LWPOLY/3DPOLY/LINE): "
            };
            var filter = new SelectionFilter(
            [
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE,LINE")
            ]);

            var sel = ed.GetSelection(opts, filter);
            if (sel.Status != PromptStatus.OK || sel.Value is null || sel.Value.Count == 0)
            {
                ed.WriteMessage("\nTCM-INZINJERING: Nista nije izabrano.");
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var handles = TerrainDefinitionStore.LoadBreaklineHandles(tr, db).ToList();
            var added = 0;
            foreach (SelectedObject so in sel.Value)
            {
                if (so?.ObjectId.IsNull != false)
                {
                    continue;
                }

                if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is not Curve)
                {
                    continue;
                }

                var h = so.ObjectId.Handle.Value;
                if (!handles.Contains(h))
                {
                    handles.Add(h);
                    added++;
                }
            }

            TerrainDefinitionStore.SaveBreaklineHandles(tr, db, handles);
            tr.Commit();

            ed.WriteMessage(
                $"\nTCM-INZINJERING: Breakline — dodato {added}, ukupno {handles.Count}. " +
                "Pokrenite TCMTERFACE da primenite na TIN.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
        }
    }

    /// <summary>Outer ili Hide granica terena (zatvorena polilinija).</summary>
    [CommandMethod("TCMTERBOUND", CommandFlags.Modal)]
    public void AddTerrainBoundary()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var ed = doc.Editor;
        var db = doc.Database;

        try
        {
            var kw = new PromptKeywordOptions("\nTip granice [Outer/Hide] <Outer>: ")
            {
                AllowNone = true
            };
            kw.Keywords.Add("Outer");
            kw.Keywords.Add("Hide");
            kw.Keywords.Default = "Outer";
            var kwResult = ed.GetKeywords(kw);
            if (kwResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            var kind = kwResult.Status == PromptStatus.OK &&
                       string.Equals(kwResult.StringResult, "Hide", StringComparison.OrdinalIgnoreCase)
                ? TerrainBoundaryKind.Hide
                : TerrainBoundaryKind.Outer;

            var peo = new PromptEntityOptions(
                kind == TerrainBoundaryKind.Outer
                    ? "\nIzaberite zatvorenu poliliniju (Outer boundary): "
                    : "\nIzaberite poliliniju (Hide boundary): ")
            {
                AllowNone = false
            };
            peo.SetRejectMessage("\nPotrebna je kriva (polyline).");
            peo.AddAllowedClass(typeof(Curve), exactMatch: false);

            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(per.ObjectId, OpenMode.ForRead) is not Curve curve || curve.IsErased)
            {
                ed.WriteMessage("\nTCM-INZINJERING: Nevalidna kriva.");
                tr.Commit();
                return;
            }

            if (curve is Polyline pl && !pl.Closed && kind == TerrainBoundaryKind.Outer)
            {
                ed.WriteMessage(
                    "\nTCM-INZINJERING: Outer granica treba zatvorenu poliliniju (Closed=Yes).");
                tr.Commit();
                return;
            }

            var list = TerrainDefinitionStore.LoadBoundaries(tr, db).ToList();
            var handle = per.ObjectId.Handle.Value;
            list.RemoveAll(b => b.Handle == handle);
            list.Add(new TerrainBoundaryRef(handle, kind));
            // Samo jedna Outer — zameni staru.
            if (kind == TerrainBoundaryKind.Outer)
            {
                list = list.Where(b => b.Kind != TerrainBoundaryKind.Outer || b.Handle == handle).ToList();
            }

            TerrainDefinitionStore.SaveBoundaries(tr, db, list);
            tr.Commit();

            ed.WriteMessage(
                $"\nTCM-INZINJERING: Granica {kind} sacuvana. Pokrenite TCMTERFACE.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
        }
    }

    /// <summary>Brise sacuvane TIN edit operacije (swap/delete), ne dira breakline/granice.</summary>
    [CommandMethod("TCMTEREDCLEAR", CommandFlags.Modal)]
    public void ClearTerrainTinEdits()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            TerrainDefinitionStore.ClearEditOps(tr, doc.Database);
            tr.Commit();
            doc.Editor.WriteMessage(
                "\nTCM-INZINJERING: TIN edit ops obrisani. TCMTERFACE koristi samo tacke + breakline/granicu.");
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\nTCM-INZINJERING greska: {ex.Message}");
        }
    }
}
