using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Compat;
using TcmInzenjering.Plugin.Update;
#if !BRICSCAD
using TcmInzenjering.Plugin.Ribbon;
#endif

[assembly: ExtensionApplication(typeof(TcmInzenjering.Plugin.PluginApplication))]
[assembly: CommandClass(typeof(TcmInzenjering.Plugin.Commands))]
[assembly: CommandClass(typeof(TcmInzenjering.Plugin.UpdateCommands))]

[assembly: CommandClass(typeof(TcmInzenjering.Plugin.Roads.RoadCommands))]

namespace TcmInzenjering.Plugin;

public sealed class PluginApplication : IExtensionApplication
{
#if !BRICSCAD
    private static bool _ribbonInitialized;
#endif

    public void Initialize()
    {
        try
        {
            PluginAssemblyResolver.Register();

#if !BRICSCAD
            // Hub=0, ostali tabovi=1 (sinhronizuje se i pomjenom ActiveTab).
            RibbonBuilder.SyncRibbonIconResizeForActiveTab();

            if (Autodesk.Windows.ComponentManager.Ribbon is not null)
            {
                EnsureRibbon();
            }
            else
            {
                Autodesk.Windows.ComponentManager.ItemInitialized += OnComponentManagerItemInitialized;
                Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle += OnIdleOnce;
            }

            Autodesk.AutoCAD.ApplicationServices.Core.Application.SystemVariableChanged += OnSystemVariableChanged;
#endif
            try
            {
                Roads.AxisChangeMonitor.Initialize();
                Roads.AxisSelectionCoordinator.Initialize();
                Roads.TerrainSelectionCoordinator.Initialize();
                Roads.StationFontPreferences.Load();
                Roads.ProjectFolderPreferences.Load();
                Roads.Terrain.ContourPreferences.Load();
            }
            catch (System.Exception ex)
            {
                WriteMessage($"TCM-INZINJERING: greska pri inicijalizaciji puteva - {ex.Message}");
            }

            WriteMessage($"TCM-INZINJERING v{PluginInfo.Version}: plugin ucitan. Komande: TCMPLO2TAN, TCMUPDATE, TCMSTACAZUR");
            System.Threading.Tasks.Task.Run(UpdateCommands.CheckForUpdatesOnStartup);
        }
        catch (System.Exception ex)
        {
            WriteMessage($"TCM-INZINJERING: greska pri ucitavanju - {ex.Message}");
        }
    }

    public void Terminate()
    {
#if !BRICSCAD
        Autodesk.Windows.ComponentManager.ItemInitialized -= OnComponentManagerItemInitialized;
        Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= OnIdleOnce;
        Autodesk.AutoCAD.ApplicationServices.Core.Application.SystemVariableChanged -= OnSystemVariableChanged;
        // Ne ostavljaj AutoCAD na RIBBONICONRESIZE=0 posle unload-a.
        RibbonBuilder.RestoreAutoCadRibbonIconResizeDefault();
#endif
        Roads.AxisChangeMonitor.Terminate();
        Roads.AxisSelectionCoordinator.Terminate();
        Roads.TerrainSelectionCoordinator.Terminate();
    }

#if !BRICSCAD
    private static void OnComponentManagerItemInitialized(object? sender, Autodesk.Windows.RibbonItemEventArgs e)
    {
        if (Autodesk.Windows.ComponentManager.Ribbon is null)
        {
            return;
        }

        EnsureRibbon();
        Autodesk.Windows.ComponentManager.ItemInitialized -= OnComponentManagerItemInitialized;
        Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= OnIdleOnce;
    }

    private static void OnIdleOnce(object? sender, System.EventArgs e)
    {
        if (Autodesk.Windows.ComponentManager.Ribbon is null)
        {
            return;
        }

        EnsureRibbon();
        Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= OnIdleOnce;
        Autodesk.Windows.ComponentManager.ItemInitialized -= OnComponentManagerItemInitialized;
    }

    private static void OnSystemVariableChanged(object sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
    {
        if (!string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ribbonInitialized = false;
        EnsureRibbon();
    }

    internal static void EnsureRibbon()
    {
        if (_ribbonInitialized)
        {
            return;
        }

        try
        {
            RibbonBuilder.CreateOrRefreshRibbonTab();
            _ribbonInitialized = true;
        }
        catch (System.Exception ex)
        {
            WriteMessage($"TCM-INZINJERING: greska pri kreiranju ribbon taba - {ex.Message}");
        }
    }
#else
    internal static void EnsureRibbon()
    {
        WriteMessage("TCM-INZINJERING: Ribbon nije dostupan u BricsCAD verziji. Koristite komande iz komandne linije.");
    }
#endif

    private static void WriteMessage(string message)
    {
        try
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage($"\n{message}");
        }
        catch
        {
            // CAD host jos nije spreman za ispis u komandnu liniju.
        }
    }
}
