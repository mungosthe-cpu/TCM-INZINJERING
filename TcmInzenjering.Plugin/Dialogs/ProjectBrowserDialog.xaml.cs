using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Roads;
using TcmInzenjering.Plugin.Roads.Profile;
using TcmInzenjering.Plugin.Roads.Terrain;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class ProjectBrowserDialog : Window
{
    private readonly List<TcmProject> _projects = [];
    private TcmProject? _current;
    private List<string> _allTerrains = [];
    private List<string> _allAxes = [];
    private List<(string Id, string Title, string Axis)> _allProfiles = [];
    private List<(string Name, int Count)> _allPointSets = [];
    private List<(string Key, string Display)> _allBoundaries = [];
    private HashSet<string> _snapshotKeys = new(StringComparer.OrdinalIgnoreCase);

    public ProjectBrowserDialog()
    {
        InitializeComponent();
        LoadFromDrawing();
    }

    private void LoadFromDrawing()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        using var tr = doc.Database.TransactionManager.StartTransaction();
        // Migracija iz starog NOD → AppData (jednom, ako je katalog prazan).
        _projects.Clear();
        _projects.AddRange(TcmProjectStore.LoadAll(tr, doc.Database));
        _allTerrains = NamedTerrainSurfaceStore.ListNames(tr, doc.Database).ToList();
        _allPointSets = [];
        foreach (var name in _allTerrains)
        {
            var pts = NamedTerrainSurfaceStore.TryLoadSurface(tr, doc.Database, name);
            _allPointSets.Add((name, pts?.Count ?? 0));
        }

        _allBoundaries = TerrainDefinitionStore.LoadBoundaries(tr, doc.Database)
            .Select(b =>
            {
                var key = TcmProjectStore.FormatBoundaryKey(b.Kind, b.Handle);
                return (key, $"{b.Kind}  (handle {b.Handle})");
            })
            .ToList();
        _snapshotKeys = TerrainBoundarySnapshotStore.LoadAll(tr, doc.Database)
            .Select(s => s.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        TerrainBoundaryHandleCache.Refresh(tr, doc.Database);

        try
        {
            _allAxes = RoadAxisStore.GetAxisNames(tr, doc.Database).ToList();
        }
        catch
        {
            _allAxes = [];
        }

        _allProfiles = ProfileViewStore.LoadAll(tr, doc.Database)
            .Select(v => (v.ProfileId, string.IsNullOrWhiteSpace(v.TableName) ? v.ProfileId : v.TableName, v.AxisName))
            .ToList();

        var activeId = TcmProjectStore.GetActiveId(tr, doc.Database);
        tr.Commit();

        foreach (var p in _projects)
        {
            p.PointSetNames ??= [];
            p.BoundaryKeys ??= [];
        }

        RefreshProjectCombo(activeId);
        RefreshAvailableLists();
        RefreshTree();
    }

    private void RefreshProjectCombo(string? preferId = null)
    {
        ProjectBox.Items.Clear();
        foreach (var p in _projects)
        {
            ProjectBox.Items.Add(p.Name);
        }

        if (_projects.Count == 0)
        {
            _current = null;
            FolderBox.Text = string.Empty;
            return;
        }

        var idx = 0;
        if (!string.IsNullOrWhiteSpace(preferId))
        {
            var found = _projects.FindIndex(p =>
                string.Equals(p.Id, preferId, StringComparison.OrdinalIgnoreCase));
            if (found >= 0)
            {
                idx = found;
            }
        }

        ProjectBox.SelectedIndex = idx;
        _current = _projects[idx];
        FolderBox.Text = _current.FolderPath;
    }

    private void RefreshAvailableLists()
    {
        TerrainList.Items.Clear();
        AxisList.Items.Clear();
        ProfileList.Items.Clear();
        PointsList.Items.Clear();
        BoundaryList.Items.Clear();

        foreach (var t in _allTerrains)
        {
            TerrainList.Items.Add(t);
        }

        foreach (var a in _allAxes)
        {
            AxisList.Items.Add(a);
        }

        foreach (var p in _allProfiles)
        {
            ProfileList.Items.Add($"{p.Title}  [{p.Axis}]  ({p.Id})");
        }

        foreach (var ps in _allPointSets)
        {
            PointsList.Items.Add($"{ps.Name}  ({ps.Count} tacaka)");
        }

        foreach (var b in _allBoundaries)
        {
            BoundaryList.Items.Add(b.Display);
        }
    }

    private void RefreshTree()
    {
        ProjectTree.Items.Clear();
        if (_current is null)
        {
            return;
        }

        _current.PointSetNames ??= [];
        _current.BoundaryKeys ??= [];

        var root = new TreeViewItem
        {
            Header = CreateTreeHeader("\uE8B7", Color.FromRgb(0x25, 0x63, 0xEB), _current.Name, null, bold: true),
            IsExpanded = true
        };

        var terrainColor = Color.FromRgb(0x16, 0xA3, 0x4A);
        var teren = new TreeViewItem
        {
            Header = CreateTreeHeader("\uE7C3", terrainColor, "Tereni", _current.TerrainNames.Count),
            IsExpanded = true
        };
        foreach (var n in _current.TerrainNames)
        {
            teren.Items.Add(new TreeViewItem
            {
                Header = CreateTreeHeader("\uE7C3", terrainColor, n, null),
                Tag = ValueTuple.Create("terrain", n)
            });
        }

        var axisColor = Color.FromRgb(0x25, 0x63, 0xEB);
        var osi = new TreeViewItem
        {
            Header = CreateTreeHeader("\uE9E9", axisColor, "Osovine", _current.AxisNames.Count),
            IsExpanded = true
        };
        foreach (var n in _current.AxisNames)
        {
            osi.Items.Add(new TreeViewItem
            {
                Header = CreateTreeHeader("\uE9E9", axisColor, n, null),
                Tag = ValueTuple.Create("axis", n)
            });
        }

        var profileColor = Color.FromRgb(0xDB, 0x27, 0x77);
        var pod = new TreeViewItem
        {
            Header = CreateTreeHeader("\uE9D2", profileColor, "Poduzni profili", _current.ProfileIds.Count),
            IsExpanded = true
        };
        for (var i = 0; i < _current.ProfileIds.Count; i++)
        {
            var id = _current.ProfileIds[i];
            var title = i < _current.ProfileTitles.Count && !string.IsNullOrWhiteSpace(_current.ProfileTitles[i])
                ? _current.ProfileTitles[i]
                : id;
            pod.Items.Add(new TreeViewItem
            {
                Header = CreateTreeHeader("\uE9D2", profileColor, $"{title} ({id})", null),
                Tag = ValueTuple.Create("profile", id)
            });
        }

        var pointsColor = Color.FromRgb(0xF5, 0x9E, 0x0B);
        var tacke = new TreeViewItem
        {
            Header = CreateTreeHeader("\uE9D9", pointsColor, "Tacke", _current.PointSetNames.Count),
            IsExpanded = true,
            ToolTip = "Dupli klik na skup otvara uredjivanje tacaka."
        };
        foreach (var n in _current.PointSetNames)
        {
            var count = _allPointSets.FirstOrDefault(p =>
                string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)).Count;
            tacke.Items.Add(new TreeViewItem
            {
                Header = CreateTreeHeader("\uEA3B", pointsColor, count > 0 ? $"{n}  ({count})" : n, null),
                Tag = ValueTuple.Create("points", n),
                ToolTip = "Dupli klik = uredjivanje tacaka"
            });
        }

        var boundaryColor = Color.FromRgb(0xDC, 0x26, 0x26);
        var granice = new TreeViewItem
        {
            Header = CreateTreeHeader("\uE7FB", boundaryColor, "Granice", _current.BoundaryKeys.Count),
            IsExpanded = true,
            ToolTip = "Crveni ! = izuzeto iz crteza. Dugmad: Primeni / Izuzmi."
        };
        foreach (var key in _current.BoundaryKeys)
        {
            var match = _allBoundaries.FirstOrDefault(b =>
                string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase));
            var present = !string.IsNullOrWhiteSpace(match.Display);
            var hasSnap = _snapshotKeys.Contains(key);

            string label;
            string? tip;
            if (present)
            {
                label = match.Display;
                tip = "U crtezu. „Izuzmi iz crteza“ uklanja liniju (ostaje u projektu sa !).";
            }
            else if (TcmProjectStore.TryParseBoundaryKey(key, out var kind, out var handle))
            {
                label = hasSnap
                    ? $"{kind}  (handle {handle})"
                    : $"{kind}  (handle {handle})  — nema snimka";
                tip = hasSnap
                    ? "Izuzeto iz crteza. „Primeni na crtez“ vraca granicu i TIN."
                    : "Izuzeto iz crteza, snimak nije dostupan u ovom DWG-u.";
            }
            else
            {
                label = key;
                tip = "Izuzeto / nepoznata granica.";
            }

            granice.Items.Add(new TreeViewItem
            {
                Header = present
                    ? CreateTreeHeader("\uE7FB", boundaryColor, label, null)
                    : CreateExcludedBoundaryHeader(label),
                Tag = ValueTuple.Create("boundary", key),
                ToolTip = tip
            });
        }

        root.Items.Add(teren);
        root.Items.Add(osi);
        root.Items.Add(pod);
        root.Items.Add(tacke);
        root.Items.Add(granice);
        ProjectTree.Items.Add(root);
    }

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectBox.SelectedIndex < 0 || ProjectBox.SelectedIndex >= _projects.Count)
        {
            return;
        }

        _current = _projects[ProjectBox.SelectedIndex];
        FolderBox.Text = _current.FolderPath;
        RefreshTree();
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        var name = PromptText("Ime novog projekta:", $"Projekat {_projects.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var project = new TcmProject
        {
            Name = name.Trim(),
            FolderPath = ProjectFolderPreferences.FolderPath
        };
        TcmProjectStore.Save(project);
        TcmProjectStore.SetActiveId(project.Id);
        LoadFromDrawing();
        RefreshProjectCombo(project.Id);
        RefreshTree();
    }

    private void OnRenameProject(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var name = PromptText("Novo ime projekta:", _current.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _current.Name = name.Trim();
        SaveCurrent();
        RefreshProjectCombo(_current.Id);
    }

    private void OnDeleteProject(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"Obrisati projekat '{_current.Name}'?\n(Elementi u crtezu ostaju — brise se samo grupisanje.)",
                "TCM-ROADS",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        TcmProjectStore.Delete(_current.Id);
        LoadFromDrawing();
    }

    private void OnAddSelected(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show(this, "Prvo kreirajte ili izaberite projekat.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (string item in TerrainList.SelectedItems)
        {
            if (!_current.TerrainNames.Exists(n =>
                    string.Equals(n, item, StringComparison.OrdinalIgnoreCase)))
            {
                _current.TerrainNames.Add(item);
            }
        }

        foreach (string item in AxisList.SelectedItems)
        {
            if (!_current.AxisNames.Exists(n =>
                    string.Equals(n, item, StringComparison.OrdinalIgnoreCase)))
            {
                _current.AxisNames.Add(item);
            }
        }

        foreach (string display in ProfileList.SelectedItems)
        {
            var match = _allProfiles.FirstOrDefault(p =>
                display.Contains($"({p.Id})"));
            if (string.IsNullOrWhiteSpace(match.Id))
            {
                continue;
            }

            if (!_current.ProfileIds.Exists(id =>
                    string.Equals(id, match.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _current.ProfileIds.Add(match.Id);
                _current.ProfileTitles.Add(match.Title);
            }
        }

        _current.PointSetNames ??= [];
        foreach (string display in PointsList.SelectedItems)
        {
            var name = ExtractPointSetName(display);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!_current.PointSetNames.Exists(n =>
                    string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                _current.PointSetNames.Add(name);
            }
        }

        _current.BoundaryKeys ??= [];
        foreach (string display in BoundaryList.SelectedItems)
        {
            var match = _allBoundaries.FirstOrDefault(b =>
                string.Equals(b.Display, display, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(match.Key))
            {
                continue;
            }

            if (!_current.BoundaryKeys.Exists(k =>
                    string.Equals(k, match.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _current.BoundaryKeys.Add(match.Key);
            }
        }

        SaveCurrent();
        RefreshTree();
    }

    private static string ExtractPointSetName(string display)
    {
        var idx = display.LastIndexOf("  (", StringComparison.Ordinal);
        return idx > 0 ? display[..idx].Trim() : display.Trim();
    }

    private void OnRemoveFromProject(object sender, RoutedEventArgs e)
    {
        if (_current is null || ProjectTree.SelectedItem is not TreeViewItem item ||
            item.Tag is not ValueTuple<string, string> tag)
        {
            MessageBox.Show(this, "Izaberite element u stablu projekta.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        switch (tag.Item1)
        {
            case "terrain":
                _current.TerrainNames.RemoveAll(n =>
                    string.Equals(n, tag.Item2, StringComparison.OrdinalIgnoreCase));
                break;
            case "axis":
                _current.AxisNames.RemoveAll(n =>
                    string.Equals(n, tag.Item2, StringComparison.OrdinalIgnoreCase));
                break;
            case "profile":
                var idx = _current.ProfileIds.FindIndex(id =>
                    string.Equals(id, tag.Item2, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    _current.ProfileIds.RemoveAt(idx);
                    if (idx < _current.ProfileTitles.Count)
                    {
                        _current.ProfileTitles.RemoveAt(idx);
                    }
                }

                break;
            case "points":
                _current.PointSetNames ??= [];
                _current.PointSetNames.RemoveAll(n =>
                    string.Equals(n, tag.Item2, StringComparison.OrdinalIgnoreCase));
                break;
            case "boundary":
                _current.BoundaryKeys ??= [];
                _current.BoundaryKeys.RemoveAll(k =>
                    string.Equals(k, tag.Item2, StringComparison.OrdinalIgnoreCase));
                break;
        }

        SaveCurrent();
        RefreshTree();
    }

    private void OnProjectTreeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProjectTree.SelectedItem is not TreeViewItem item ||
            item.Tag is not ValueTuple<string, string> tag)
        {
            return;
        }

        if (tag.Item1 == "points")
        {
            e.Handled = true;
            OpenPointSetEditor(tag.Item2);
            return;
        }

        if (tag.Item1 == "terrain")
        {
            e.Handled = true;
            PreviewTerrainPoints(tag.Item2);
            return;
        }

        if (tag.Item1 == "boundary")
        {
            e.Handled = true;
            TryApplyBoundary(tag.Item2);
        }
    }

    private void OnPreviewTerrainPoints(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not TreeViewItem item ||
            item.Tag is not ValueTuple<string, string> tag ||
            tag.Item1 is not ("terrain" or "points" or "axis" or "profile"))
        {
            MessageBox.Show(this,
                "Izaberite teren, tačke, osovinu ili podužni profil u stablu projekta.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (tag.Item1 is "terrain" or "points")
        {
            PreviewTerrainPoints(tag.Item2);
            return;
        }

        ZoomToProjectElement(tag.Item1, tag.Item2);
    }

    private void OnAvailableTerrainDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TerrainList.SelectedItem is not string terrainName)
        {
            return;
        }

        e.Handled = true;
        PreviewTerrainPoints(terrainName);
    }

    private void PreviewTerrainPoints(string surfaceName)
    {
        Visibility = System.Windows.Visibility.Hidden;
        try
        {
            if (!TerrainPointPreview.Show(surfaceName, out var error))
            {
                MessageBox.Show(this,
                    error ?? "Tacke terena nisu dostupne.",
                    "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            Visibility = System.Windows.Visibility.Visible;
            Activate();
        }
    }

    private void ZoomToProjectElement(string kind, string key)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        Visibility = System.Windows.Visibility.Hidden;
        try
        {
            var ids = new List<ObjectId>();
            Extents3d? combined = null;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),
                    OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
                    {
                        continue;
                    }

                    var matches = kind == "axis"
                        ? RoadXData.TryReadAxisElement(entity, out var axisName, out _) &&
                          string.Equals(axisName, key, StringComparison.OrdinalIgnoreCase)
                        : ProfileXData.TryReadRole(entity, out _, out var profileId) &&
                          string.Equals(profileId, key, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                    {
                        continue;
                    }

                    try
                    {
                        var extents = entity.GeometricExtents;
                        if (combined is null)
                        {
                            combined = extents;
                        }
                        else
                        {
                            var value = combined.Value;
                            value.AddExtents(extents);
                            combined = value;
                        }

                        ids.Add(id);
                    }
                    catch
                    {
                        // Entitet bez validnog extenta se samo preskače.
                    }
                }

                tr.Commit();
            }

            if (combined is null)
            {
                MessageBox.Show(this,
                    kind == "axis"
                        ? "Geometrija izabrane osovine nije pronađena u crtežu."
                        : "Geometrija izabranog podužnog profila nije pronađena u crtežu.",
                    "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            doc.Editor.SetImpliedSelection(ids.ToArray());
            ZoomToExtents(doc.Editor, combined.Value, 0.08);
            doc.Editor.UpdateScreen();
            doc.Editor.GetPoint(new PromptPointOptions(
                kind == "axis"
                    ? $"\nTCM-ROADS: Osovina '{key}' je prikazana. Kliknite za povratak u Projekat."
                    : $"\nTCM-ROADS: Podužni profil je prikazan. Kliknite za povratak u Projekat.")
            {
                AllowNone = true
            });
            doc.Editor.SetImpliedSelection(Array.Empty<ObjectId>());
        }
        finally
        {
            Visibility = System.Windows.Visibility.Visible;
            Activate();
        }
    }

    private static void ZoomToExtents(Editor editor, Extents3d extents, double marginRatio)
    {
        using var view = editor.GetCurrentView();
        var worldToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
        worldToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * worldToDcs;
        worldToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * worldToDcs;
        extents.TransformBy(worldToDcs.Inverse());

        var width = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, 1.0);
        var height = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, 1.0);
        var margin = Math.Max(0, marginRatio);
        view.Width = width * (1 + 2 * margin);
        view.Height = height * (1 + 2 * margin);
        view.CenterPoint = new Point2d(
            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
        editor.SetCurrentView(view);
    }

    private void OnApplyBoundaryToDrawing(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedBoundaryKey(out var key))
        {
            MessageBox.Show(this,
                "Izaberite granicu sa ! u stablu, zatim „Primeni na crtez“.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TryApplyBoundary(key);
    }

    private void OnExcludeBoundaryFromDrawing(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedBoundaryKey(out var key))
        {
            MessageBox.Show(this,
                "Izaberite granicu u stablu, zatim „Izuzmi iz crteza“.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TryExcludeBoundary(key);
    }

    private bool TryGetSelectedBoundaryKey(out string key)
    {
        key = string.Empty;
        if (ProjectTree.SelectedItem is not TreeViewItem item ||
            item.Tag is not ValueTuple<string, string> tag ||
            tag.Item1 != "boundary")
        {
            return false;
        }

        key = tag.Item2;
        return true;
    }

    private void TryApplyBoundary(string boundaryKey)
    {
        var present = _allBoundaries.Any(b =>
            string.Equals(b.Key, boundaryKey, StringComparison.OrdinalIgnoreCase));
        if (present)
        {
            MessageBox.Show(this, "Granica je vec u crtezu.", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_snapshotKeys.Contains(boundaryKey))
        {
            MessageBox.Show(this,
                "Nema snimka granice u ovom crtezu — ne moze se primeniti.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RunBoundaryAction(() =>
        {
            if (!TerrainBoundaryRestore.TryRestore(boundaryKey, out var message))
            {
                MessageBox.Show(this, message, "TCM-ROADS",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void TryExcludeBoundary(string boundaryKey)
    {
        var present = _allBoundaries.Any(b =>
            string.Equals(b.Key, boundaryKey, StringComparison.OrdinalIgnoreCase));
        if (!present)
        {
            MessageBox.Show(this, "Granica je vec izuzeta iz crteza (vidi !).", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RunBoundaryAction(() =>
        {
            if (!TerrainBoundaryExclude.TryExclude(boundaryKey, out var message))
            {
                MessageBox.Show(this, message, "TCM-ROADS",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void RunBoundaryAction(Action action)
    {
        Visibility = System.Windows.Visibility.Hidden;
        try
        {
            action();
        }
        finally
        {
            Visibility = System.Windows.Visibility.Visible;
            Activate();
            LoadFromDrawing();
            if (_current is not null)
            {
                RefreshProjectCombo(_current.Id);
                RefreshTree();
            }
        }
    }

    /// <summary>Red u stablu: ikonica u boji + tekst + sivi badge sa brojem (kao u mockup-u).</summary>
    private static FrameworkElement CreateTreeHeader(
        string glyph,
        Color iconColor,
        string label,
        int? count,
        bool bold = false)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2)
        };

        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = new SolidColorBrush(iconColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (count is not null)
        {
            row.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 1, 8, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = count.Value.ToString(),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        return row;
    }

    /// <summary>Crveni bedž „!“ + tekst — uočljivo kad je granica izuzeta iz crteža.</summary>
    private static FrameworkElement CreateExcludedBoundaryHeader(string label)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "!",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        text.Inlines.Add(new Run(label));
        text.Inlines.Add(new Run("  · izuzeto")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            FontWeight = FontWeights.Bold,
            FontSize = 11
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(badge);
        row.Children.Add(text);
        return row;
    }

    private void OpenPointSetEditor(string surfaceName)
    {
        try
        {
            Visibility = System.Windows.Visibility.Hidden;
            RoadCommands.OpenNamedTerrainPointsEditor(surfaceName, owner: this);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Visibility = System.Windows.Visibility.Visible;
            Activate();
            LoadFromDrawing();
            if (_current is not null)
            {
                RefreshProjectCombo(_current.Id);
                RefreshTree();
            }
        }
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        if (ProjectFolderPreferences.TryPickFolder(this, out var folder))
        {
            _current.FolderPath = folder;
            FolderBox.Text = folder;
            SaveCurrent();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        SaveCurrent();
        MessageBox.Show(this, "Projekat sacuvan (vidljiv u svim crtezima).", "TCM-ROADS",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void SaveCurrent()
    {
        if (_current is null)
        {
            return;
        }

        TcmProjectStore.Save(_current);
        TcmProjectStore.SetActiveId(_current.Id);
    }

    private string? PromptText(string prompt, string initial)
    {
        var win = new Window
        {
            Title = "TCM-ROADS",
            Width = 360,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        var box = new TextBox
        {
            Text = initial,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(box);
        var ok = new Button
        {
            Content = "OK",
            Width = 80,
            Height = 26,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) =>
        {
            win.DialogResult = true;
            win.Close();
        };
        panel.Children.Add(ok);
        win.Content = panel;
        box.SelectAll();
        box.Focus();
        return win.ShowDialog() == true ? box.Text : null;
    }
}
