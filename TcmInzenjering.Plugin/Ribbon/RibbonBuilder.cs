using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Ribbon;

/// <summary>
/// CGS Labs stil: hub tab TCM-ROADS (SITUACIJA, Poduzni profil) + sekundarni tabovi.
/// </summary>
internal static class RibbonBuilder
{
    public const string TabId = "TCM_ROADS_TAB";
    public const string TabTitle = "TCM-ROADS";

    public const string SituacijaTabId = "TCM_SITUACIJA_TAB";
    public const string SituacijaTabTitle = "Situacija-TCM";

    public const string TerenTabId = "TCM_TEREN_TAB";
    public const string TerenTabTitle = "Teren-TCM";

    public const string PoduzniProfilTabId = "TCM_PODUZNI_PROFIL_TAB";
    public const string PoduzniProfilTabTitle = "Poduzni-TCM";

    /// <summary>Civil kontekstualni tab — „Tin Surface: ime“.</summary>
    public const string TinSurfaceTabId = "TCM_TIN_SURFACE_TAB";

    private const string FeaturedAppsTitle = "Featured Apps";

    private static bool _hubResizeHooksAttached;

    public static void CreateOrRefreshRibbonTab()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        RemoveTab(ribbon, TabId);
        RemoveTab(ribbon, "TCM_INZINJERING_TAB"); // stari hub id
        RemoveTab(ribbon, TerenTabId);
        RemoveTab(ribbon, SituacijaTabId);
        RemoveTab(ribbon, PoduzniProfilTabId);
        RemoveTab(ribbon, TinSurfaceTabId);
        _hubResizeHooksAttached = false;

        var hub = BuildHubTab();
        var teren = BuildTerenTab();
        var situacija = BuildSituacijaTab();
        var poduzni = BuildPoduzniProfilTab();
        var tinSurface = BuildTinSurfaceTab("Surface");

        InsertTabNearFeaturedApps(ribbon, hub);
        var hubIndex = IndexOfTab(ribbon, TabId);
        if (hubIndex >= 0)
        {
            ribbon.Tabs.Insert(hubIndex + 1, teren);
            ribbon.Tabs.Insert(hubIndex + 2, situacija);
            ribbon.Tabs.Insert(hubIndex + 3, poduzni);
            ribbon.Tabs.Insert(hubIndex + 4, tinSurface);
        }
        else
        {
            ribbon.Tabs.Add(teren);
            ribbon.Tabs.Add(situacija);
            ribbon.Tabs.Add(poduzni);
            ribbon.Tabs.Add(tinSurface);
        }

        AttachRibbonIconResizeHooks(hub, teren, situacija, poduzni, tinSurface);
        SyncRibbonIconResizeForActiveTab();
    }

    /// <summary>Civil: prikaži kontekstualni tab „Tin Surface: {name}“ i aktiviraj ga.</summary>
    public static void ShowTinSurfaceTab(string surfaceName)
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(surfaceName) ? "Surface" : surfaceName.Trim();
        var tab = ribbon.FindTab(TinSurfaceTabId);
        if (tab is null)
        {
            CreateOrRefreshRibbonTab();
            tab = ribbon.FindTab(TinSurfaceTabId);
        }

        if (tab is null)
        {
            return;
        }

        tab.Title = $"Tin Surface: {name}";
        tab.IsVisible = true;
        ribbon.ActiveTab = tab;
    }

    public static void HideTinSurfaceTab()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        var tab = ribbon.FindTab(TinSurfaceTabId);
        if (tab is null)
        {
            return;
        }

        var wasActive = ReferenceEquals(ribbon.ActiveTab, tab);
        tab.IsVisible = false;
        if (!wasActive)
        {
            return;
        }

        // Vrati se na Teren modul ako je otvoren, inače hub.
        var teren = ribbon.FindTab(TerenTabId);
        if (teren is not null && teren.IsVisible)
        {
            ribbon.ActiveTab = teren;
            return;
        }

        var hub = ribbon.FindTab(TabId);
        if (hub is not null)
        {
            ribbon.ActiveTab = hub;
        }
    }

    public static void ActivateSituacijaTab() => ActivateSecondaryTab(SituacijaTabId);

    public static void ActivateTerenTab() => ActivateSecondaryTab(TerenTabId);

    public static void ActivatePoduzniProfilTab() => ActivateSecondaryTab(PoduzniProfilTabId);

    public static void CloseSituacijaTab() => CloseSecondaryTab(SituacijaTabId);

    public static void CloseTerenTab() => CloseSecondaryTab(TerenTabId);

    public static void ClosePoduzniProfilTab() => CloseSecondaryTab(PoduzniProfilTabId);

    private static void CloseSecondaryTab(string tabId)
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null)
        {
            return;
        }

        var secondary = ribbon.FindTab(tabId);
        if (secondary is not null)
        {
            secondary.IsVisible = false;
        }

        var hub = ribbon.FindTab(TabId);
        if (hub is not null)
        {
            ribbon.ActiveTab = hub;
        }
    }

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
        RemoveTab(ribbon, "TCM_INZINJERING_TAB"); // stari hub id
        RemoveTab(ribbon, TerenTabId);
        RemoveTab(ribbon, SituacijaTabId);
        RemoveTab(ribbon, PoduzniProfilTabId);
        RemoveTab(ribbon, TinSurfaceTabId);
    }

    private static RibbonTab BuildHubTab()
    {
        var tab = new RibbonTab
        {
            Id = TabId,
            Title = TabTitle
        };

        AddPanel(tab, "TEREN",
            CreateModuleButton(
                "Teren-TCM",
                "Crtanje 3D terena od tacaka (3DFACE / TIN).",
                "teren",
                TerenTabId,
                new TerenModuleHandler()));

        AddPanel(tab, "SITUACIJA",
            CreateModuleButton(
                "Situacija-TCM",
                "Otvara alate za situacioni plan (osovina, stacionaza...).",
                "situacija",
                SituacijaTabId,
                new SituacijaModuleHandler()));

        AddPanel(tab, "PODUZNI PROFIL",
            CreateModuleButton(
                "Poduzni-TCM",
                "Otvara alate za poduzni profil (tabela + teren).",
                "poduzni_profil",
                PoduzniProfilTabId,
                new PoduzniProfilModuleHandler()));

        AddPanel(tab, "PROJEKAT",
            CreateLargeCommandButton(
                "PROJEKAT",
                "Pregled elemenata projekta (teren, osovina, poduzni).",
                "tcm_projekat",
                "TCMPROJEKAT "));

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

    /// <summary>Civil Tin Surface kontekst — alati za selektovani imenovani teren.</summary>
    private static RibbonTab BuildTinSurfaceTab(string surfaceName)
    {
        var tab = new RibbonTab
        {
            Id = TinSurfaceTabId,
            Title = $"Tin Surface: {surfaceName}",
            IsVisible = false
        };

        AddPanel(tab, "Modify",
            CreateCommandButton(
                "3DFACE / rebuild",
                "Ponovo gradi TIN i border iz tacaka aktivnog terena.",
                "TCMTERFACE ",
                "tin_surface"),
            CreateAddTinLineSplitButton(),
            CreateCommandButton(
                "Swap 3DFACE",
                "Zamenjuje zajednicku ivicu (Civil Swap Edge).",
                "TCMTERSWAP ",
                "tin_line"),
            CreateCommandButton(
                "Brisi 3DFACE",
                "Brise TIN ivice/trouglove.",
                "TCMTERBRISI ",
                "erase_sample_lines"));

        AddPanel(tab, "Analyze / Display",
            CreateContourSplitButton(),
            CreateCommandButton(
                "Podesavanja stila",
                "Civil Surface Style (Contours / Display / Borders…).",
                "TCMTERIZOSET ",
                "podesavanja"),
            CreateCommandButton(
                "Slope",
                "Nagib (boje + strelice) na aktivnom TIN-u.",
                "TCMTERSLOPE ",
                "slope"),
            CreateCommandButton(
                "Watershed",
                "Slivovi (watershed) na aktivnom TIN-u.",
                "TCMTERWSHD ",
                "watershed"),
            CreateCommandButton(
                "Zapremina",
                "Panel poverenja zapremine (TIN + Grid + sekcije).",
                "TCMTERZAP ",
                "volume"),
            CreateCommandButton(
                "Kotne oznake",
                "Kotna oznaka na izohipsi.",
                "TCMTERIZOLBL ",
                "label_contours"),
            CreateCommandButton(
                "Spot elevacija",
                "Z na kliknutoj tacki terena.",
                "TCMTERSPOT ",
                "spot_elev"));

        AddPanel(tab, "Teren",
            CreateCommandButton(
                "Tacke",
                "Otvara dijalog tacaka / imenovanih terena.",
                "TCMTERUREDI ",
                "tcm_add_terrain_points"),
            CreateModuleButton(
                "Modul TEREN",
                "Otvara pun Teren tab.",
                "teren",
                TerenTabId,
                new TerenModuleHandler()));

        return tab;
    }

    private static RibbonTab BuildTerenTab()
    {
        var tab = new RibbonTab
        {
            Id = TerenTabId,
            Title = TerenTabTitle,
            IsVisible = false
        };

        AddPanel(tab, "3D teren",
            CreateTerrainPointsSplitButton(),
            CreateCommandButton(
                "3DFACE teren",
                "Delaunay TIN — tacke + breakline + granica + sacuvani swap/delete.",
                "TCMTERFACE ",
                "tin_surface"),
            CreateAddTinLineSplitButton(),
            CreateCommandButton(
                "Swap 3DFACE",
                "Zamenjuje zajednicku ivicu dva trougla (kao Civil 3D Swap Edge).",
                "TCMTERSWAP ",
                "tin_line"),
            CreateCommandButton(
                "Brisi 3DFACE",
                "Brise TIN ivice/trouglove (kao Civil 3D Delete Line).",
                "TCMTERBRISI ",
                "erase_sample_lines"));

        AddPanel(tab, "Definicija",
            CreateCommandButton(
                "Breakline",
                "Polilinije kao obavezne TIN ivice (Civil/Plateia breakline).",
                "TCMTERBREAK ",
                "breakline"),
            CreateCommandButton(
                "Breakline lejer",
                "Spoji tacke terena duz linija na lejeru i swap TIN da ne sece liniju.",
                "TCMTERBRKLAY ",
                "breakline"),
            CreateCommandButton(
                "Granica",
                "Outer ili Hide granica terena (zatvorena polilinija).",
                "TCMTERBOUND ",
                "boundary"),
            CreateCommandButton(
                "Ocisti TIN edit",
                "Brise sacuvane swap/delete operacije (breakline/granica ostaju).",
                "TCMTEREDCLEAR ",
                "refresh"));

        AddPanel(tab, "Projekcija",
            CreateCommandButton(
                "Projekcija na teren",
                "Projektuje osovinu na 3D teren (Face/Mesh/Tin Surface).",
                "TCMPROJTER ",
                "drape"));

        AddPanel(tab, "Geodezija",
            CreateSurveyGridSplitButton());

        AddPanel(tab, "Podloga",
            CreateCommandButton(
                "Podloga",
                "Georeferencirana satelitska / ortofoto podloga (Autodesk Esri ili WMS/ArcGIS/lokalni fajl).",
                "TCMTERMAP ",
                "dtm"));

        AddPanel(tab, "Izohipse",
            CreateContourSplitButton(),
            CreateCommandButton(
                "Kotne oznake",
                "Dodaje kotnu oznaku na izabranu izohipsu.",
                "TCMTERIZOLBL ",
                "label_contours"),
            CreateCommandButton(
                "Spot elevacija",
                "Prikazuje Z na kliknutoj tacki terena.",
                "TCMTERSPOT ",
                "spot_elev"));

        AddPanel(tab, "Analiza",
            CreateCommandButton(
                "Slope",
                "Boji 3DFACE po nagibu i crta strelice padine (Civil Slope).",
                "TCMTERSLOPE ",
                "slope"),
            CreateCommandButton(
                "Watershed",
                "Crta slivove (watershed) na TIN-u — tok ka suncu depresije / granici.",
                "TCMTERWSHD ",
                "watershed"),
            CreateCommandButton(
                "Zapremina",
                "Panel poverenja: TIN–TIN + Grid + sekcije, mapa neslaganja, izvestaj.",
                "TCMTERZAP ",
                "volume"));

        AddPanel(tab, "Zatvori",
            CreateCloseButton(
                "Zatvori",
                "Zatvara tab Teren i vraca na TCM-ROADS.",
                new CloseTerenHandler()));

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
            CreateCommandButton(
                "Best Fit osovina",
                "Aproksimira snimljenu polylinu čistim PI pravcima i kreira TCM osovinu.",
                "TCMBESTFIT ",
                "plo2tan"),
            CreateCommandButton(
                "Rucno uredjivanje zaobljenja",
                "UredjenjeKrivine — Auto/Ručno (LRL, LR, RL, LL); pick R sa crteza (✕).",
                "TCMZAOUREDI ",
                "curve_edit"),
            CreateCommandButton("Stacionaze", "Oznake stacionaze duz ose.", "TCMSTACOZN ", "station_labels"),
            CreateCommandButton("Azuriraj stac.", "Azurira stacionaze posle pomeranja.", "TCMSTACAZUR ", "refresh_labels"));

        AddPanel(tab, "Info / tabela",
            CreateCommandButton("Info osovine", "Tabela elemenata u komandnoj liniji.", "TCMOSINFO ", "alignment_data"),
            CreateCommandButton("Tabela osovine", "Ubacuje tabelu elemenata u crtez.", "TCMOSTAB ", "tables_manager"));

        AddPanel(tab, "Kolovoz",
            CreateCommandButton(
                "Sirine traka",
                "Upravljanje tipovima i sirinama levih/desnih traka aktivne ose.",
                "TCMKOLSIR ",
                "sample_lines"));

        AddPanel(tab, "Teren",
            CreateCommandButton("Projekcija na teren", "Projektuje osovinu na 3D teren.", "TCMPROJTER ", "drape"));

        AddPanel(tab, "Poprecne ose",
            CreateCommandButton("Pozicija pop. osa", "Polozaj oznaka i stacionaza.", "TCMPOPOSPOZ ", "sample_label_settings"),
            CreateCrossAxisEditSplitButton());

        AddPanel(tab, "Zatvori",
            CreateCloseButton(
                "Zatvori",
                "Zatvara tab Situacija i vraca na TCM-ROADS.",
                new CloseSituacijaHandler()));

        return tab;
    }

    /// <summary>Podužni profil — tabela + teren iz projektovane nivelete (Plateia stil).</summary>
    private static RibbonTab BuildPoduzniProfilTab()
    {
        var tab = new RibbonTab
        {
            Id = PoduzniProfilTabId,
            Title = PoduzniProfilTabTitle,
            IsVisible = false
        };

        AddPanel(tab, "Profil",
            CreateCommandButton(
                "Tabela + teren",
                "CGSA Unos terena: dijalog, grafik i banderola (OZNAKE/STAC/KOTE TERENA).",
                "TCMPODCRT ",
                "draw_profile"),
            CreateCommandButton(
                "Samo tabela",
                "Tabela i mreža bez linije terena. Teren: TCMPODTER. Niveleta: TCMNIVODTER.",
                "TCMPODTAB ",
                "profile_table"),
            CreateCommandButton(
                "Teren u profil",
                "Ponovo crta samo zeleni teren u postojeci profil.",
                "TCMPODTER ",
                "terrain_in_profile"));

        AddPanel(tab, "Niveleta",
            CreateCommandButton(
                "Niveleta od terena",
                "Kreira projektovanu niveletu od TCMPROJTER + ofset (TCMNIVODTER).",
                "TCMNIVODTER ",
                "draw_profile"),
            CreateCommandButton(
                "Uredi PVI",
                "Rucni unos / izmena PVI nivelete (TCMNIVUREDI).",
                "TCMNIVUREDI ",
                "profile_table"));

        AddPanel(tab, "Poprecni",
            CreateCommandButton(
                "Poprecni profili",
                "Crta section views iz pop. osa + TIN (+ niveleta) — TCMPOPPRF.",
                "TCMPOPPRF ",
                "sample_lines"));

        AddPanel(tab, "Zatvori",
            CreateCloseButton(
                "Zatvori",
                "Zatvara tab Poduzni profil i vraca na TCM-ROADS.",
                new ClosePoduzniProfilHandler()));

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

    /// <summary>
    /// Uređivanje pop. osa: Dodavanje (TCMPOPSTAC) / Brisanje (TCMPOPBRISI).
    /// </summary>
    private static RibbonSplitButton CreateCrossAxisEditSplitButton()
    {
        const string icon = "sample_lines";
        var split = new RibbonSplitButton
        {
            Id = "TCM_CROSS_AXIS_EDIT",
            Text = "Uredjivanje pop. osa",
            Description = "Dodavanje i brisanje poprecnih osa na situaciji.",
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            IsSplit = true,
            IsSynchronizedWithCurrentItem = false,
            ListButtonStyle = Autodesk.Private.Windows.RibbonListButtonStyle.SplitButton
        };

        var dodavanje = CreateSplitMenuCommand(
            "Dodavanje poprecnih osa",
            "Crtanje poprecnih osa na stacionazi (TCMPOPSTAC).",
            "TCMPOPSTAC ",
            "sample_lines");
        var brisanje = CreateSplitMenuCommand(
            "Brisanje poprecnih osa",
            "Brise poprecne ose preko tabele ili izbora na crtezu (TCMPOPBRISI).",
            "TCMPOPBRISI ",
            "erase_sample_lines");

        split.Items.Add(dodavanje);
        split.Items.Add(brisanje);
        split.Current = dodavanje;

        var large = RibbonIconLoader.LoadLarge(icon)
                    ?? RibbonIconLoader.LoadLarge("projekcija");
        var small = RibbonIconLoader.LoadSmall(icon) ?? large;
        if (large is not null)
        {
            split.LargeImage = large;
        }

        if (small is not null)
        {
            split.Image = small;
        }

        split.ToolTip = new RibbonToolTip
        {
            Title = "Uredjivanje pop. osa",
            Content = "Dodavanje / Brisanje poprecnih osa na situaciji."
        };

        return split;
    }

    /// <summary>
    /// TIN edit: Add Line (jedan segment) / Add Continuous Line (lanac).
    /// </summary>
    private static RibbonSplitButton CreateAddTinLineSplitButton()
    {
        const string icon = "tin_line";
        var split = new RibbonSplitButton
        {
            Id = "TCM_ADD_TIN_LINE",
            Text = "Dodaj liniju",
            Description = "Forsira TIN ivicu — jedna linija ili neprekidni lanac.",
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            IsSplit = true,
            IsSynchronizedWithCurrentItem = true,
            ListButtonStyle = Autodesk.Private.Windows.RibbonListButtonStyle.SplitButton
        };

        var addLine = CreateSplitMenuCommand(
            "Dodaj liniju",
            "Jedna TIN ivica izmedju dva temena (ili duz linije).",
            "TCMTERADDLINE ",
            icon);
        var addContinuous = CreateSplitMenuCommand(
            "Neprekidna linija",
            "Neprekidni lanac TIN ivica — biraj temena redom, Enter = kraj lanca.",
            "TCMTERADDCLINE ",
            icon);

        split.Items.Add(addLine);
        split.Items.Add(addContinuous);
        split.Current = addLine;

        var large = RibbonIconLoader.LoadNative($"{icon}_32")
                    ?? RibbonIconLoader.LoadLarge(icon)
                    ?? RibbonIconLoader.LoadLarge("projekcija");
        var small = RibbonIconLoader.LoadNative($"{icon}_16")
                    ?? RibbonIconLoader.LoadSmall(icon)
                    ?? large;
        if (large is not null)
        {
            split.LargeImage = large;
        }

        if (small is not null)
        {
            split.Image = small;
        }

        split.ToolTip = new RibbonToolTip
        {
            Title = "Dodaj liniju",
            Content = "Dodaj liniju / Neprekidna linija — forsira TIN ivice."
        };

        return split;
    }

    /// <summary>
    /// Civil Contours stil: Crtaj izohipse + Podešavanja (intervali, smooth, boje).
    /// </summary>
    private static RibbonSplitButton CreateContourSplitButton()
    {
        const string icon = "contours";
        var split = new RibbonSplitButton
        {
            Id = "TCM_TERRAIN_CONTOURS",
            Text = "Izohipse",
            Description = "Izohipse iz TCM TIN-a — crtanje i stil (Civil Surface Style).",
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            IsSplit = true,
            IsSynchronizedWithCurrentItem = false,
            ListButtonStyle = Autodesk.Private.Windows.RibbonListButtonStyle.SplitButton
        };

        var crtaj = CreateSplitMenuCommand(
            "Crtaj izohipse",
            "Crta major/minor/user izohipse prema stilu terena.",
            "TCMTERIZO ",
            icon);
        var podesavanja = CreateSplitMenuCommand(
            "Podesavanja",
            "Civil Surface Style: Contours, Display, Borders, Grid, Points…",
            "TCMTERIZOSET ",
            "podesavanja");

        split.Items.Add(crtaj);
        split.Items.Add(new RibbonSeparator());
        split.Items.Add(podesavanja);
        split.Current = crtaj;

        var large = RibbonIconLoader.LoadNative($"{icon}_32")
                    ?? RibbonIconLoader.LoadLarge(icon)
                    ?? RibbonIconLoader.LoadLarge("projekcija");
        var small = RibbonIconLoader.LoadNative($"{icon}_16")
                    ?? RibbonIconLoader.LoadSmall(icon)
                    ?? large;
        if (large is not null)
        {
            split.LargeImage = large;
        }

        if (small is not null)
        {
            split.Image = small;
        }

        split.ToolTip = new RibbonToolTip
        {
            Title = "Izohipse",
            Content = "Crtaj izohipse / Podešavanja (Civil Surface Style)."
        };

        return split;
    }

    /// <summary>
    /// Geodetski raster: krstovi sa koordinatama ili DBPoint tačke u rasteru.
    /// </summary>
    private static RibbonSplitButton CreateSurveyGridSplitButton()
    {
        const string icon = "alignment_data";
        var split = new RibbonSplitButton
        {
            Id = "TCM_SURVEY_GRID",
            Text = "Geo raster",
            Description = "Geodetski krstovi i tacke u koordinatnom rasteru.",
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            IsSplit = true,
            IsSynchronizedWithCurrentItem = false,
            ListButtonStyle = Autodesk.Private.Windows.RibbonListButtonStyle.SplitButton
        };

        var crosses = CreateSplitMenuCommand(
            "Geodetski krstovi",
            "Crta i kotira geodetske krstove u WCS koordinatnom rasteru.",
            "TCMGEOKRST ",
            icon);
        var points = CreateSplitMenuCommand(
            "Tacke koordinatnog rastera",
            "Crta DBPoint tacke u pravilnom WCS koordinatnom rasteru.",
            "TCMKOORASTER ",
            "spot_elev");

        split.Items.Add(crosses);
        split.Items.Add(new RibbonSeparator());
        split.Items.Add(points);
        split.Current = crosses;

        var large = RibbonIconLoader.LoadNative($"{icon}_32")
                    ?? RibbonIconLoader.LoadLarge(icon)
                    ?? RibbonIconLoader.LoadLarge("spot_elev");
        var small = RibbonIconLoader.LoadNative($"{icon}_16")
                    ?? RibbonIconLoader.LoadSmall(icon)
                    ?? large;
        if (large is not null)
        {
            split.LargeImage = large;
        }

        if (small is not null)
        {
            split.Image = small;
        }

        split.ToolTip = new RibbonToolTip
        {
            Title = "Geo raster",
            Content = "Geodetski krstovi / tacke koordinatnog rastera."
        };

        return split;
    }

    /// <summary>
    /// Civil Points stil: veliko dugme + padajuci meni (ikona tcm_add_terrain_points).
    /// </summary>
    private static RibbonSplitButton CreateTerrainPointsSplitButton()
    {
        const string icon = "tcm_add_terrain_points";
        var split = new RibbonSplitButton
        {
            Id = "TCM_ADD_TERRAIN_POINTS",
            Text = "Tacke",
            Description = "Tacke terena — izbor, blokovi, ucitavanje i snimanje (kao Civil Points).",
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Large,
            Orientation = Orientation.Vertical,
            IsSplit = true,
            IsSynchronizedWithCurrentItem = false,
            ListButtonStyle = Autodesk.Private.Windows.RibbonListButtonStyle.SplitButton
        };

        var izaberi = CreateSplitMenuCommand(
            "Izaberi tacke",
            "Izabira tacke iz crteza i zamenjuje trenutni skup terena.",
            "TCMTERIZABERI ",
            icon);
        var dodaj = CreateSplitMenuCommand(
            "Dodaj tacke",
            "Dodaje nove tacke u postojeci skup terena.",
            "TCMTERDODAJ ",
            icon);
        var dodajBlok = CreateSplitMenuCommand(
            "Dodaj tacku kao blok",
            "Jedan blok = sablon: atribut = Z, pa obelezavanje istih blokova kao tacke terena.",
            "TCMTERBLOK ",
            icon);
        var krugUTacku = CreateSplitMenuCommand(
            "Krug u tacku",
            "Uzorak kruga + oblast: svi krugovi na istom lejeru postaju imenovana grupa tacaka terena.",
            "TCMTERKRUGTAC ",
            "krug_u_tacku");
        var ucitaj = CreateSplitMenuCommand(
            "Ucitaj tacke",
            "Ucitava XYZ CSV/TXT u ovaj crtez (folder projekta).",
            "TCMTERUCITAJ ",
            icon);
        var uredi = CreateSplitMenuCommand(
            "Uredi tacke",
            "Otvara prozor tacaka (pregled / izmena) bez novog izbora.",
            "TCMTERUREDI ",
            "podesavanja");
        var snimi = CreateSplitMenuCommand(
            "Snimi tacke",
            "Snima XYZ tacke (CSV) u folder projekta.",
            "TCMTERSNIMI ",
            "refresh");

        split.Items.Add(izaberi);
        split.Items.Add(dodaj);
        split.Items.Add(dodajBlok);
        split.Items.Add(krugUTacku);
        split.Items.Add(ucitaj);
        split.Items.Add(new RibbonSeparator());
        split.Items.Add(uredi);
        split.Items.Add(snimi);

        // Glavni klik = Izaberi tacke; strelica otvara meni.
        split.Current = izaberi;

        var large = RibbonIconLoader.LoadNative($"{icon}_32")
                    ?? RibbonIconLoader.LoadLarge(icon)
                    ?? RibbonIconLoader.LoadNative($"{icon}_48")
                    ?? RibbonIconLoader.LoadNative($"{icon}_64")
                    ?? RibbonIconLoader.LoadLarge("dodaj_tacku");
        var small = RibbonIconLoader.LoadNative($"{icon}_16")
                    ?? RibbonIconLoader.LoadSmall(icon)
                    ?? large;
        if (large is not null)
        {
            split.LargeImage = large;
        }

        if (small is not null)
        {
            split.Image = small;
        }

        split.ToolTip = new RibbonToolTip
        {
            Title = "Tacke terena",
            Content = "Izaberi / Dodaj / Blok / Ucitaj / Uredi / Snimi."
        };

        return split;
    }

    private static RibbonButton CreateSplitMenuCommand(
        string text,
        string description,
        string command,
        string iconName)
    {
        var button = new RibbonButton
        {
            Text = text,
            Description = description,
            ShowText = true,
            ShowImage = true,
            Size = RibbonItemSize.Standard,
            Id = "TCM_SPLIT_" + Math.Abs(command.GetHashCode()).ToString("X"),
            CommandHandler = new RibbonCommandHandler(),
            CommandParameter = command,
            ToolTip = new RibbonToolTip
            {
                Title = text,
                Content = description
            }
        };

        var icon = RibbonIconLoader.LoadSmall(iconName)
                   ?? RibbonIconLoader.LoadLarge(iconName)
                   ?? RibbonIconLoader.LoadNative($"{iconName}_16")
                   ?? RibbonIconLoader.LoadNative($"{iconName}_32");
        if (icon is not null)
        {
            button.Image = icon;
            button.LargeImage = RibbonIconLoader.LoadLarge(iconName) ?? icon;
        }

        return button;
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

    private static RibbonButton CreateCloseButton(string text, string description, ICommand handler)
    {
        // Mala ikona (Standard) — panel „Zatvori“; bez velikog 64px dugmeta.
        var button = new RibbonButton
        {
            Text = text,
            Description = description,
            ShowText = false,
            ShowImage = true,
            Size = RibbonItemSize.Standard,
            Orientation = Orientation.Horizontal,
            AllowInStatusBar = false,
            AllowInToolBar = true,
            Id = "TCM_CLOSE_TAB",
            CommandHandler = handler,
            ToolTip = new RibbonToolTip
            {
                Title = text,
                Content = description
            }
        };

        var small = RibbonIconLoader.LoadNative("close_16")
                    ?? RibbonIconLoader.LoadNative("tcm_close_16")
                    ?? RibbonIconLoader.LoadSmall("close")
                    ?? RibbonIconLoader.LoadSmall("tcm_close");
        var large = RibbonIconLoader.LoadNative("close_32")
                    ?? RibbonIconLoader.LoadNative("tcm_close_32")
                    ?? RibbonIconLoader.LoadLarge("close")
                    ?? RibbonIconLoader.LoadLarge("tcm_close")
                    ?? small;

        if (small is not null)
        {
            button.Image = small;
        }

        if (large is not null)
        {
            button.LargeImage = large;
        }

        return button;
    }

    /// <summary>
    /// Hub TCM-ROADS: RIBBONICONRESIZE=0 (lepe TCM ikone).
    /// Bilo koji drugi tab (TCM moduli ili AutoCAD/CGSA): vrati 1.
    /// </summary>
    internal static void SyncRibbonIconResizeForActiveTab()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            var hubActive = ribbon?.ActiveTab is not null &&
                            string.Equals(ribbon.ActiveTab.Id, TabId, StringComparison.OrdinalIgnoreCase);
            SetRibbonIconResize(hubActive ? 0 : 1);
        }
        catch
        {
            SetRibbonIconResize(1);
        }
    }

    /// <summary>Kompatibilnost: van hub taba uvek default AutoCAD (=1).</summary>
    internal static void RestoreAutoCadRibbonIconResizeDefault() => SetRibbonIconResize(1);

    private static void AttachRibbonIconResizeHooks(params RibbonTab[] tabs)
    {
        if (_hubResizeHooksAttached)
        {
            return;
        }

        foreach (var tab in tabs)
        {
            if (tab is null)
            {
                continue;
            }

            tab.Activated -= OnTcmRibbonTabActivated;
            tab.Deactivated -= OnTcmRibbonTabDeactivated;
            tab.Activated += OnTcmRibbonTabActivated;
            tab.Deactivated += OnTcmRibbonTabDeactivated;
        }

        _hubResizeHooksAttached = true;
    }

    private static void OnTcmRibbonTabActivated(object? sender, EventArgs e) =>
        SyncRibbonIconResizeForActiveTab();

    private static void OnTcmRibbonTabDeactivated(object? sender, EventArgs e)
    {
        // Hub napustjen → odmah 1 (modul / drugi AutoCAD tab).
        // Kad se hub ponovo aktivira, Activated vraca 0.
        if (sender is RibbonTab tab &&
            string.Equals(tab.Id, TabId, StringComparison.OrdinalIgnoreCase))
        {
            SetRibbonIconResize(1);
            return;
        }

        SyncRibbonIconResizeForActiveTab();
    }

    private static void SetRibbonIconResize(int value)
    {
        try
        {
            var current = AcApp.GetSystemVariable("RIBBONICONRESIZE");
            var existing = current switch
            {
                short s => (int)s,
                int i => i,
                long l => (int)l,
                double d => (int)d,
                _ => -1
            };

            if (existing == value)
            {
                return;
            }

            AcApp.SetSystemVariable("RIBBONICONRESIZE", value);
        }
        catch
        {
            // Sysvar nedostupan.
        }
    }

    /// <summary>
    /// Hub dugmad: preference 64px (RADI sa RIBBONICONRESIZE=0 na hub tabu).
    /// </summary>
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

internal sealed class TerenModuleHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => RibbonBuilder.ActivateTerenTab();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class CloseSituacijaHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => RibbonBuilder.CloseSituacijaTab();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class CloseTerenHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => RibbonBuilder.CloseTerenTab();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class ClosePoduzniProfilHandler : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => RibbonBuilder.ClosePoduzniProfilTab();

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
                "TCM-ROADS",
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
