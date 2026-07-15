using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Ribbon;

/// <summary>
/// CGS Labs stil: hub tab (TCM-INŽINJERING) sa modulom SITUACIJA;
/// klik na SITUACIJA aktivira sekundarni tab sa alatima.
/// </summary>
internal static class RibbonBuilder
{
    public const string TabId = "TCM_INZINJERING_TAB";
    public const string TabTitle = "TCM-INŽINJERING";

    public const string SituacijaTabId = "TCM_SITUACIJA_TAB";
    public const string SituacijaTabTitle = "Situacija";

    private const string FeaturedAppsTitle = "Featured Apps";

    public static void CreateOrRefreshRibbonTab()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        RemoveTab(ribbon, TabId);
        RemoveTab(ribbon, SituacijaTabId);

        var hub = BuildHubTab();
        var situacija = BuildSituacijaTab();

        InsertTabNearFeaturedApps(ribbon, hub);
        // Situacija odmah iza hub taba (kao Plateia → Situacija kod CGS-a).
        var hubIndex = IndexOfTab(ribbon, TabId);
        if (hubIndex >= 0)
        {
            ribbon.Tabs.Insert(hubIndex + 1, situacija);
        }
        else
        {
            ribbon.Tabs.Add(situacija);
        }
    }

    /// <summary>Aktivira sekundarni tab Situacija (poziva se kada korisnik klikne SITUACIJA).</summary>
    public static void ActivateSituacijaTab()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        var tab = ribbon.FindTab(SituacijaTabId);
        if (tab is null)
        {
            CreateOrRefreshRibbonTab();
            tab = ribbon.FindTab(SituacijaTabId);
        }

        if (tab is not null)
        {
            tab.IsVisible = true;
            ribbon.ActiveTab = tab;
        }
    }

    public static void RemoveAllTcmTabs()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        RemoveTab(ribbon, TabId);
        RemoveTab(ribbon, SituacijaTabId);
    }

    private static RibbonTab BuildHubTab()
    {
        var tab = new RibbonTab
        {
            Id = TabId,
            Title = TabTitle
        };

        // Kao "CGS Labs produkti" — za sad samo jedan modul.
        AddPanel(tab, "Moduli",
            CreateModuleButton(
                "SITUACIJA",
                "Otvara alate za situacioni plan (osovina, stacionaza, teren...).",
                "situacija"));

        // Kao "Aktivacija i ažuriranje" / pomoć.
        AddPanel(tab, "Info i nadogradnja",
            CreateCommandButton("Verzija", "Info o programu.", "TCMINFO ", "info"),
            CreateCommandButton("Nadogradnja", "Provera nove verzije.", "TCMUPDATE ", "refresh"));

        AddPanel(tab, "Alati",
            CreateCommandButton("Podesavanja", "Font ispisa stacionaze.", "TCMSTACFONT ", "toolspace"),
            CreateCommandButton("Osvezi Ribbon", "Ponovo kreira ribbon tabove.", "TCMRIBBON ", "ribbon"),
            CreateCommandButton("Deinstaliraj", "Brise plugin iz AutoCAD-a.", "TCMUNINSTALL ", "uninstall"));

        return tab;
    }

    private static RibbonTab BuildSituacijaTab()
    {
        var tab = new RibbonTab
        {
            Id = SituacijaTabId,
            Title = SituacijaTabTitle,
            // Kao CGS: sekundarni tab se pojavi tek kad korisnik klikne modul.
            IsVisible = false
        };

        AddPanel(tab, "Osovina",
            CreateCommandButton("PLO u tangentni poligon", "Pretvara polylinu u osovinu.", "TCMPLO2TAN ", "plo2tan"),
            CreateCommandButton("Stacionaze", "Oznake stacionaze duz ose.", "TCMSTACOZN ", "staco"),
            CreateCommandButton("Azuriraj stac.", "Azurira stacionaze posle pomeranja.", "TCMSTACAZUR ", "refresh"));

        AddPanel(tab, "Info / tabela",
            CreateCommandButton("Info osovine", "Tabela elemenata u komandnoj liniji.", "TCMOSINFO ", "info"),
            CreateCommandButton("Tabela osovine", "Ubacuje tabelu elemenata u crtez.", "TCMOSTAB ", "info"));

        AddPanel(tab, "Teren",
            CreateCommandButton("Projekcija na teren", "Projektuje osovinu na 3D teren.", "TCMPROJTER ", "projekcija"));

        AddPanel(tab, "Poprecne ose",
            CreateCommandButton("Pozicija pop. osa", "Polozaj oznaka i stacionaza.", "TCMPOPOSPOZ ", "staco"));

        AddPanel(tab, "Test",
            CreateCommandButton("Test plugin", "Provera da li je plugin ucitan.", "TCMHELLO ", "hello"));

        return tab;
    }

    private static void AddPanel(RibbonTab tab, string panelTitle, params RibbonButton[] buttons)
    {
        var panelSource = new RibbonPanelSource { Title = panelTitle };
        foreach (var button in buttons)
        {
            panelSource.Items.Add(button);
        }

        tab.Panels.Add(new RibbonPanel { Source = panelSource });
    }

    private static RibbonButton CreateModuleButton(string text, string description, string iconName)
    {
        var button = CreateButtonBase(text, description, iconName);
        button.Id = "TCM_MODULE_" + iconName.ToUpperInvariant();
        button.CommandHandler = new SituacijaModuleHandler();
        button.CommandParameter = SituacijaTabId;
        return button;
    }

    private static RibbonButton CreateCommandButton(string text, string description, string command, string iconName)
    {
        var button = CreateButtonBase(text, description, iconName);
        button.Id = "TCM_CMD_" + Math.Abs(command.GetHashCode()).ToString("X");
        button.CommandHandler = new RibbonCommandHandler();
        button.CommandParameter = command;
        return button;
    }

    private static RibbonButton CreateButtonBase(string text, string description, string iconName)
    {
        var button = new RibbonButton
        {
            Text = text,
            Description = description,
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            AllowInStatusBar = false,
            AllowInToolBar = true
        };

        var large = RibbonIconLoader.LoadLarge(iconName)
                    ?? RibbonIconLoader.LoadLarge("toolspace")
                    ?? RibbonIconLoader.LoadLarge("plo2tan");
        var small = RibbonIconLoader.LoadSmall(iconName)
                    ?? RibbonIconLoader.LoadSmall("toolspace")
                    ?? RibbonIconLoader.LoadSmall("plo2tan");

        if (large is not null)
        {
            button.LargeImage = large;
        }

        if (small is not null)
        {
            button.Image = small;
        }
        else if (large is not null)
        {
            button.Image = large;
        }

        return button;
    }

    private static void RemoveTab(RibbonControl ribbon, string tabId)
    {
        var tab = ribbon.FindTab(tabId);
        if (tab is not null)
        {
            ribbon.Tabs.Remove(tab);
        }
    }

    private static int IndexOfTab(RibbonControl ribbon, string tabId)
    {
        for (var i = 0; i < ribbon.Tabs.Count; i++)
        {
            if (string.Equals(ribbon.Tabs[i].Id, tabId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static void InsertTabNearFeaturedApps(RibbonControl ribbon, RibbonTab tab)
    {
        var insertIndex = FindFeaturedAppsIndex(ribbon);
        if (insertIndex >= 0)
        {
            ribbon.Tabs.Insert(insertIndex + 1, tab);
            return;
        }

        ribbon.Tabs.Add(tab);
    }

    private static int FindFeaturedAppsIndex(RibbonControl ribbon)
    {
        for (var i = 0; i < ribbon.Tabs.Count; i++)
        {
            var title = ribbon.Tabs[i].Title ?? string.Empty;
            if (title.IndexOf(FeaturedAppsTitle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Klik na SITUACIJA → aktivira sekundarni tab Situacija.</summary>
internal sealed class SituacijaModuleHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => RibbonBuilder.ActivateSituacijaTab();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class RibbonCommandHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        var command = parameter switch
        {
            string s => s,
            RibbonButton button => button.CommandParameter as string,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        doc.SendStringToExecute(command, true, false, true);
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
