using Autodesk.AutoCAD.Runtime;
using TcmInzenjering.Plugin.Update;
#if NET8_0_OR_GREATER
using TcmInzenjering.Plugin.Ribbon;
#endif

[assembly: ExtensionApplication(typeof(TcmInzenjering.Plugin.PluginApplication))]
[assembly: CommandClass(typeof(TcmInzenjering.Plugin.Commands))]
[assembly: CommandClass(typeof(TcmInzenjering.Plugin.UpdateCommands))]

[assembly: CommandClass(typeof(TcmInzenjering.Plugin.Roads.RoadCommands))]

namespace TcmInzenjering.Plugin;

public sealed class PluginApplication : IExtensionApplication
{
#if NET8_0_OR_GREATER
    private static bool _ribbonInitialized;
#endif

    public void Initialize()
    {
        try
        {
#if NET8_0_OR_GREATER
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
            Roads.AxisChangeMonitor.Initialize();
            Roads.AxisSelectionCoordinator.Initialize();
            Roads.StationFontPreferences.Load();
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
#if NET8_0_OR_GREATER
        Autodesk.Windows.ComponentManager.ItemInitialized -= OnComponentManagerItemInitialized;
        Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= OnIdleOnce;
        Autodesk.AutoCAD.ApplicationServices.Core.Application.SystemVariableChanged -= OnSystemVariableChanged;
#endif
        Roads.AxisChangeMonitor.Terminate();
        Roads.AxisSelectionCoordinator.Terminate();
    }

#if NET8_0_OR_GREATER
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
        WriteMessage("TCM-INZINJERING: Ribbon nije dostupan u legacy/BricsCAD verziji. Koristite komande iz komandne linije.");
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
