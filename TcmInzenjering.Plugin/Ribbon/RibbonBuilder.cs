using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Ribbon;

internal static class RibbonBuilder
{
    public const string TabId = "TCM_INZINJERING_TAB";
    public const string TabTitle = "TCM-INZINJERING";

    private const string FeaturedAppsTitle = "Featured Apps";

    public static void CreateOrRefreshRibbonTab()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        var existingTab = ribbon.FindTab(TabId);
        if (existingTab is not null)
        {
            ribbon.Tabs.Remove(existingTab);
        }

        var tab = new RibbonTab
        {
            Id = TabId,
            Title = TabTitle
        };

        AddPanel(tab, "Podesavanja", panel =>
        {
            AddButtonsToRow(panel, CreateButton(
                "Font stacionaze",
                "Definise font ispisa oznaka stacionaze.",
                "TCMSTACFONT ",
                "toolspace"));
        });

        AddPanel(tab, "Putovi", panel =>
        {
            AddButtonsToRow(
                panel,
                CreateButton("PLO u tangentni poligon", "Pretvara polylinu u osovinu (pravac+luk) i stacionaze.", "TCMPLO2TAN ", "plo2tan"),
                CreateButton("Stacionaze", "Iscrtava oznake stacionaze duz polyline osovine.", "TCMSTACOZN ", "staco"),
                CreateButton("Azuriraj stac.", "Azurira stacionaze posle pomeranja osovine.", "TCMSTACAZUR ", "refresh"),
                CreateButton("Info osovine", "Prikaz tabele elemenata osovine u komandnoj liniji.", "TCMOSINFO ", "info"),
                CreateButton("Tabela osovine", "Ubacuje tabelu elemenata osovine u crtez.", "TCMOSTAB ", "info"),
                CreateButton("Test plugin", "Provera da li je plugin ucitan.", "TCMHELLO ", "hello"));
        });

        AddPanel(tab, "Uredjenje osa", panel =>
        {
            AddButtonsToRow(panel, CreateButton(
                "Pozicija pop. osa",
                "Podesava polozaj oznaka i stacionaza poprecnih osa.",
                "TCMPOPOSPOZ ",
                "staco"));
        });

        AddPanel(tab, "Alati", panel =>
        {
            AddButtonsToRow(
                panel,
                CreateButton("Nadogradnja", "Proverava da li postoji novija verzija plugina.", "TCMUPDATE ", "refresh"),
                CreateButton("Verzija", "Prikaz trenutne verzije i linka za preuzimanje.", "TCMINFO ", "info"),
                CreateButton("Osvezi Ribbon", "Ponovo kreira ribbon tab.", "TCMRIBBON ", "ribbon"));
        });

        InsertTabNearFeaturedApps(ribbon, tab);
    }

    private static void AddPanel(RibbonTab tab, string panelTitle, Action<RibbonPanelSource> configure)
    {
        var panelSource = new RibbonPanelSource
        {
            Title = panelTitle
        };

        configure(panelSource);

        tab.Panels.Add(new RibbonPanel { Source = panelSource });
    }

    private static void AddButtonsToRow(RibbonPanelSource panel, params RibbonButton[] buttons)
    {
        var row = new RibbonRowPanel();
        foreach (var button in buttons)
        {
            row.Items.Add(button);
        }

        panel.Items.Add(row);
    }

    private static RibbonButton CreateButton(string text, string description, string command, string iconName)
    {
        var button = new RibbonButton
        {
            Text = text,
            Description = description,
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            CommandHandler = new RibbonCommandHandler(),
            CommandParameter = command
        };

        var icon = RibbonIconLoader.Load(iconName);
        if (icon is not null)
        {
            button.LargeImage = icon;
            button.Image = icon;
        }

        return button;
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
            if (title.Contains(FeaturedAppsTitle, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
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
