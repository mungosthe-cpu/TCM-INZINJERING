using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Ribbon;

/// <summary>
/// CGS Labs stil: hub tab TCM-INŽINJERING (SITUACIJA, Poduzni profil) + sekundarni tabovi.
/// </summary>
internal static class RibbonBuilder
{
    public const string TabId = "TCM_INZINJERING_TAB";
    public const string TabTitle = "TCM-INŽINJERING";

    public const string SituacijaTabId = "TCM_SITUACIJA_TAB";
    public const string SituacijaTabTitle = "Situacija";

    public const string PoduzniProfilTabId = "TCM_PODUZNI_PROFIL_TAB";
    public const string PoduzniProfilTabTitle = "Poduzni profil";

    private const string FeaturedAppsTitle = "Featured Apps";

    public static void CreateOrRefreshRibbonTab()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        TryEnableNativeRibbonIconSize();

        RemoveTab(ribbon, TabId);
        RemoveTab(ribbon, SituacijaTabId);
        RemoveTab(ribbon, PoduzniProfilTabId);

        var hub = BuildHubTab();
        var situacija = BuildSituacijaTab();
        var poduzni = BuildPoduzniProfilTab();

        InsertTabNearFeaturedApps(ribbon, hub);
        var hubIndex = IndexOfTab(ribbon, TabId);
        if (hubIndex >= 0)
        {
            ribbon.Tabs.Insert(hubIndex + 1, situacija);
            ribbon.Tabs.Insert(hubIndex + 2, poduzni);
        }
        else
        {
            ribbon.Tabs.Add(situacija);
            ribbon.Tabs.Add(poduzni);
        }
    }

    public static void ActivateSituacijaTab() => ActivateSecondaryTab(SituacijaTabId);

    public static void ActivatePoduzniProfilTab() => ActivateSecondaryTab(PoduzniProfilTabId);

    private static void ActivateSecondaryTab(string tabId)
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        var tab = ribbon.FindTab(tabId);
        if (tab is null)
        {
            CreateOrRefreshRibbonTab();
            tab = ribbon.FindTab(tabId);
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
        RemoveTab(ribbon, PoduzniProfilTabId);
    }

    private static RibbonTab BuildHubTab()
    {
        var tab = new RibbonTab
        {
            Id = TabId,
            Title = TabTitle
        };

        AddPanel(tab, "SITUACIJA",
            CreateModuleButton(
                "SITUACIJA",
                "Otvara alate za situacioni plan (osovina, stacionaza, teren...).",
                "situacija",
                SituacijaTabId,
                new SituacijaModuleHandler()));

        AddPanel(tab, "PODUZNI PROFIL",
            CreateModuleButton(
                "PODUZNI PROFIL",
                "Otvara alate za poduzni profil (jos u pripremi).",
                "poduzni_profil",
                PoduzniProfilTabId,
                new PoduzniProfilModuleHandler()));

        AddPanel(tab, "INFO",
            CreateLargeCommandButton(
                "INFO",
                "Info o programu i verziji.",
                "info",
                "TCMINFO "));

        AddPanel(tab, "PODESAVANJA",
            CreateLargeCommandButton(
                "PODESAVANJA",
                "Font ispisa stacionaze i ostale opcije.",
                "podesavanja",
                "TCMSTACFONT "));

        AddPanel(tab, "NADOGRADNJA",
            CreateLargeCommandButton(
                "NADOGRADNJA",
                "Provera nove verzije.",
                "nadogradnja",
                "TCMUPDATE "));

        AddPanel(tab, "DEINSTALACIJA",
            CreateLargeCommandButton(
                "DEINSTALACIJA",
                "Brise plugin iz AutoCAD-a.",
                "deinstalacija",
                "TCMUNINSTALL "));

        return tab;
    }

    private static RibbonTab BuildSituacijaTab()
    {
        var tab = new RibbonTab
        {
            Id = SituacijaTabId,
            Title = SituacijaTabTitle,
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

    /// <summary>Prazan tab — stavke za poduzni profil jos nisu definisane.</summary>
    private static RibbonTab BuildPoduzniProfilTab()
    {
        var tab = new RibbonTab
        {
            Id = PoduzniProfilTabId,
            Title = PoduzniProfilTabTitle,
            IsVisible = false
        };

        AddPanel(tab, "Profil",
            CreatePlaceholderButton("Uskoro", "Komande poduznog profila jos nisu dostupne."));

        AddPanel(tab, "Oznake",
            CreatePlaceholderButton("Uskoro", "Oznake poduznog profila — u pripremi."));

        AddPanel(tab, "Tabele",
            CreatePlaceholderButton("Uskoro", "Tabele poduznog profila — u pripremi."));

        return tab;
    }

    private static void AddPanel(RibbonTab tab, string panelTitle, params RibbonItem[] items)
    {
        var panelSource = new RibbonPanelSource { Title = panelTitle };
        foreach (var item in items)
        {
            panelSource.Items.Add(item);
        }

        tab.Panels.Add(new RibbonPanel { Source = panelSource });
    }

    private static RibbonButton CreateModuleButton(
        string text,
        string description,
        string iconName,
        string tabId,
        ICommand handler)
    {
        var button = new RibbonButton
        {
            Text = text,
            Description = description,
            ShowText = false,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            AllowInStatusBar = false,
            AllowInToolBar = true,
            Id = "TCM_MODULE_" + iconName.ToUpperInvariant(),
            CommandHandler = handler,
            CommandParameter = tabId,
            ToolTip = new RibbonToolTip
            {
                Title = text,
                Content = description
            }
        };

        ApplyNativeSizedIcons(button, iconName);
        return button;
    }

    /// <summary>Velika ikona (_64) bez teksta na dugmetu — panel naslov nosi ime (kao moduli).</summary>
    private static RibbonButton CreateLargeCommandButton(
        string text,
        string description,
        string iconName,
        string command)
    {
        var button = new RibbonButton
        {
            Text = text,
            Description = description,
            ShowText = false,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            AllowInStatusBar = false,
            AllowInToolBar = true,
            Id = "TCM_LARGE_" + iconName.ToUpperInvariant(),
            CommandHandler = new RibbonCommandHandler(),
            CommandParameter = command,
            ToolTip = new RibbonToolTip
            {
                Title = text,
                Content = description
            }
        };

        ApplyNativeSizedIcons(button, iconName);
        return button;
    }

    private static void ApplyNativeSizedIcons(RibbonButton button, string iconName)
    {
        var large = RibbonIconLoader.LoadNative($"{iconName}_64")
                    ?? RibbonIconLoader.LoadNative($"{iconName}_48")
                    ?? RibbonIconLoader.LoadNative($"{iconName}_32")
                    ?? RibbonIconLoader.LoadNative(iconName)
                    ?? RibbonIconLoader.LoadLarge(iconName);
        var small = RibbonIconLoader.LoadNative($"{iconName}_16")
                    ?? RibbonIconLoader.LoadSmall(iconName)
                    ?? large;

        if (large is not null)
        {
            button.LargeImage = large;
        }

        if (small is not null)
        {
            button.Image = small;
        }
    }

    private static RibbonButton CreatePlaceholderButton(string text, string description)
    {
        var button = new RibbonButton
        {
            Text = text,
            Description = description,
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            Id = "TCM_PLACEHOLDER_" + Math.Abs(description.GetHashCode()).ToString("X"),
            CommandHandler = new PlaceholderCommandHandler(),
            CommandParameter = description,
            ToolTip = new RibbonToolTip
            {
                Title = text,
                Content = description
            }
        };

        var icon = RibbonIconLoader.LoadLarge("info")
                   ?? RibbonIconLoader.LoadLarge("toolspace");
        if (icon is not null)
        {
            button.LargeImage = icon;
            button.Image = RibbonIconLoader.LoadSmall("info") ?? icon;
        }

        return button;
    }

    private static void TryEnableNativeRibbonIconSize()
    {
        try
        {
            var current = AcApp.GetSystemVariable("RIBBONICONRESIZE");
            var value = current switch
            {
                short s => (int)s,
                int i => i,
                long l => (int)l,
                double d => (int)d,
                _ => 1
            };

            if (value != 0)
            {
                AcApp.SetSystemVariable("RIBBONICONRESIZE", 0);
            }
        }
        catch
        {
            // Starije verzije bez sysvar-a.
        }
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

internal sealed class SituacijaModuleHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => RibbonBuilder.ActivateSituacijaTab();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class PoduzniProfilModuleHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => RibbonBuilder.ActivatePoduzniProfilTab();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Placeholder dugme — samo obavestenje, bez komande.</summary>
internal sealed class PlaceholderCommandHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        var msg = parameter as string ?? "Ova funkcija jos nije dostupna.";
        try
        {
            System.Windows.MessageBox.Show(
                msg,
                "TCM-INŽINJERING",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch
        {
            // UI nije kritican.
        }
    }

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
