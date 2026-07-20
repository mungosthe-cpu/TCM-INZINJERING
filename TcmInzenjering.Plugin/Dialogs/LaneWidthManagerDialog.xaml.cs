using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TcmInzenjering.Plugin.Roads.Profile;

namespace TcmInzenjering.Plugin.Dialogs;

public enum LaneWidthCloseAction
{
    Cancelled,
    Confirmed,
    PickWidthStation,
    PickSegmentStart,
    PickSegmentEnd
}

public sealed class LaneWidthPointRow
{
    public double Station { get; set; }
    public string LaneId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Width { get; set; } = 3.5;
}

public partial class LaneWidthManagerDialog : Window
{
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x72));
    private static readonly Brush LeftBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x6F, 0xDB));
    private static readonly Brush RightBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x9E, 0x56));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x46, 0x57));

    private readonly Dictionary<string, LaneWidthType> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<LaneWidthLane> _left = [];
    private readonly ObservableCollection<LaneWidthLane> _right = [];
    private readonly ObservableCollection<LaneWidthPointRow> _points = [];
    private readonly ObservableCollection<LaneTypeAssignment> _assignments = [];
    private bool _changingType;
    private bool _drawingPreview;
    private string _activeName;
    private short _hatchColor = 8;

    public LaneWidthCloseAction CloseAction { get; private set; } = LaneWidthCloseAction.Cancelled;
    public LaneWidthDefinitionSet Result { get; private set; }
    public double PendingStation { get; set; }

    public LaneWidthManagerDialog(
        string axisName,
        LaneWidthDefinitionSet definitions,
        double? pendingStation = null)
    {
        InitializeComponent();
        AxisNameText.Text = axisName;
        PreviewTitleText.Text = $"Poprečni prikaz – {axisName}";
        LeftGrid.ItemsSource = _left;
        RightGrid.ItemsSource = _right;
        PointsGrid.ItemsSource = _points;
        AssignmentsGrid.ItemsSource = _assignments;

        foreach (var type in definitions.Types)
        {
            _types[type.Name] = type.Clone();
        }

        if (_types.Count == 0)
        {
            _types["Trenutni"] = new LaneWidthType
            {
                Name = "Trenutni",
                Left = [NewLane("L1", "TRAKA_L1")],
                Right = [NewLane("R1", "TRAKA_D1")]
            };
        }

        _activeName = _types.ContainsKey(definitions.ActiveTypeName)
            ? definitions.ActiveTypeName
            : _types.Keys.First();

        foreach (var assignment in definitions.Assignments)
        {
            _assignments.Add(assignment.Clone());
        }

        WideningEnabledBox.IsChecked = definitions.Widening.Enabled;
        SpeedBox.Text = definitions.Widening.DesignSpeedKmh.ToString("0.###", CultureInfo.InvariantCulture);
        ManualLeftBox.Text = definitions.Widening.ManualDeltaLeft.ToString("0.###", CultureInfo.InvariantCulture);
        ManualRightBox.Text = definitions.Widening.ManualDeltaRight.ToString("0.###", CultureInfo.InvariantCulture);
        TransitionBox.Text = definitions.Widening.TransitionLength.ToString("0.###", CultureInfo.InvariantCulture);

        DrawBoundariesBox.IsChecked = definitions.DrawBoundaries;
        HatchEnabledBox.IsChecked = definitions.Hatch.Enabled;
        HatchScaleBox.Text = definitions.Hatch.Scale.ToString("0.###", CultureInfo.InvariantCulture);
        HatchAngleBox.Text = definitions.Hatch.Angle.ToString("0.###", CultureInfo.InvariantCulture);
        _hatchColor = definitions.Hatch.ColorIndex;
        SelectHatchPattern(definitions.Hatch.Pattern);
        UpdateHatchSwatch();

        TemplateBox.Items.Clear();
        TemplateBox.Items.Add("(šablon…)");
        foreach (var template in LaneWidthDefinitionStore.BuiltInTemplates())
        {
            TemplateBox.Items.Add(template.Name);
        }

        TemplateBox.SelectedIndex = 0;

        LoadWorkingType(_types[_activeName]);
        RefreshTypeBox();
        RebuildPointRows();
        Result = BuildResult();

        if (pendingStation is double station)
        {
            PendingStation = station;
            _points.Add(new LaneWidthPointRow
            {
                Station = station,
                LaneId = _left.FirstOrDefault()?.Id ?? _right.FirstOrDefault()?.Id ?? "L1",
                Label = _left.FirstOrDefault()?.Label ?? _right.FirstOrDefault()?.Label ?? "TRAKA",
                Width = _left.FirstOrDefault()?.Width ?? 3.5
            });
            MainTabs.SelectedIndex = 1;
        }

        Loaded += (_, _) => DrawPreview();
    }

    public LaneWidthDefinitionSet ApplyPickedStation(
        LaneWidthCloseAction action,
        double station)
    {
        PendingStation = station;
        if (action == LaneWidthCloseAction.PickWidthStation)
        {
            var lane = _left.FirstOrDefault() ?? _right.FirstOrDefault();
            _points.Add(new LaneWidthPointRow
            {
                Station = station,
                LaneId = lane?.Id ?? "L1",
                Label = lane?.Label ?? "TRAKA",
                Width = lane?.Width ?? 3.5
            });
            PushPointsIntoWorkingType();
            SaveWorkingType(_activeName);
            Result = BuildResult();
            return Result;
        }

        if (_assignments.Count == 0)
        {
            _assignments.Add(new LaneTypeAssignment
            {
                StartStation = station,
                EndStation = station + 20,
                TypeName = _activeName
            });
        }
        else
        {
            var row = _assignments[^1];
            if (action == LaneWidthCloseAction.PickSegmentStart)
            {
                row.StartStation = station;
            }
            else
            {
                row.EndStation = station;
            }
        }

        SaveWorkingType(_activeName);
        Result = BuildResult();
        return Result;
    }

    private void OnTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingType || TypeBox.SelectedItem is not string selected ||
            string.Equals(selected, _activeName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CommitGrids();
        PushPointsIntoWorkingType();
        SaveWorkingType(_activeName);
        _activeName = selected;
        LoadWorkingType(_types[selected]);
        RebuildPointRows();
        DrawPreview();
    }

    private void OnTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TemplateBox.SelectedIndex <= 0 ||
            TemplateBox.SelectedItem is not string name)
        {
            return;
        }

        var template = LaneWidthDefinitionStore.BuiltInTemplates()
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return;
        }

        LoadWorkingType(template);
        RebuildPointRows();
        DrawPreview();
        TemplateBox.SelectedIndex = 0;
    }

    private void OnSaveType(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        PushPointsIntoWorkingType();
        var name = TypeBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowWarning("Unesite naziv tipa.");
            return;
        }

        if (!TryValidateLanes(out var error))
        {
            ShowWarning(error);
            return;
        }

        SaveWorkingType(name);
        _activeName = name;
        RefreshTypeBox();
        DrawPreview();
    }

    private void OnMirror(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        var leftCopy = _left.Select(lane => lane.Clone()).ToList();
        var rightCopy = _right.Select(lane => lane.Clone()).ToList();
        _left.Clear();
        _right.Clear();
        var index = 1;
        foreach (var lane in rightCopy)
        {
            lane.Id = $"L{index}";
            lane.Label = FlipLabel(lane.Label, "L", "D");
            _left.Add(lane);
            index++;
        }

        index = 1;
        foreach (var lane in leftCopy)
        {
            lane.Id = $"R{index}";
            lane.Label = FlipLabel(lane.Label, "D", "L");
            _right.Add(lane);
            index++;
        }

        RebuildPointRows();
        DrawPreview();
    }

    private void OnDeleteType(object sender, RoutedEventArgs e)
    {
        if (_types.Count <= 1)
        {
            ShowWarning("Mora ostati najmanje jedan tip traka.");
            return;
        }

        _types.Remove(_activeName);
        _activeName = _types.Keys.First();
        LoadWorkingType(_types[_activeName]);
        RebuildPointRows();
        RefreshTypeBox();
        DrawPreview();
    }

    private void OnAddLeft(object sender, RoutedEventArgs e)
    {
        try
        {
            _left.Add(NewLane($"L{_left.Count + 1}", $"TRAKA_L{_left.Count + 1}"));
            DrawPreview();
        }
        catch
        {
            // ignore
        }
    }

    private void OnAddRight(object sender, RoutedEventArgs e)
    {
        try
        {
            _right.Add(NewLane($"R{_right.Count + 1}", $"TRAKA_D{_right.Count + 1}"));
            DrawPreview();
        }
        catch
        {
            // ignore
        }
    }

    private void OnDeleteLeft(object sender, RoutedEventArgs e) => DeleteSelected(LeftGrid, _left);
    private void OnDeleteRight(object sender, RoutedEventArgs e) => DeleteSelected(RightGrid, _right);
    private void OnMoveLeftUp(object sender, RoutedEventArgs e) => MoveSelected(LeftGrid, _left, -1);
    private void OnMoveLeftDown(object sender, RoutedEventArgs e) => MoveSelected(LeftGrid, _left, 1);
    private void OnMoveRightUp(object sender, RoutedEventArgs e) => MoveSelected(RightGrid, _right, -1);
    private void OnMoveRightDown(object sender, RoutedEventArgs e) => MoveSelected(RightGrid, _right, 1);

    private void OnAddPoint(object sender, RoutedEventArgs e)
    {
        var lane = _left.FirstOrDefault() ?? _right.FirstOrDefault();
        _points.Add(new LaneWidthPointRow
        {
            Station = PendingStation,
            LaneId = lane?.Id ?? "L1",
            Label = lane?.Label ?? "TRAKA",
            Width = lane?.Width ?? 3.5
        });
    }

    private void OnDeletePoint(object sender, RoutedEventArgs e)
    {
        if (PointsGrid.SelectedItem is LaneWidthPointRow row)
        {
            _points.Remove(row);
        }
    }

    private void OnPickPointStation(object sender, RoutedEventArgs e) =>
        CloseForPick(LaneWidthCloseAction.PickWidthStation);

    private void OnAddAssignment(object sender, RoutedEventArgs e)
    {
        _assignments.Add(new LaneTypeAssignment
        {
            StartStation = 0,
            EndStation = 100,
            TypeName = _activeName
        });
    }

    private void OnDeleteAssignment(object sender, RoutedEventArgs e)
    {
        if (AssignmentsGrid.SelectedItem is LaneTypeAssignment row)
        {
            _assignments.Remove(row);
        }
    }

    private void OnPickSegmentStart(object sender, RoutedEventArgs e) =>
        CloseForPick(LaneWidthCloseAction.PickSegmentStart);

    private void OnPickSegmentEnd(object sender, RoutedEventArgs e) =>
        CloseForPick(LaneWidthCloseAction.PickSegmentEnd);

    private void OnPickHatchColor(object sender, RoutedEventArgs e)
    {
        AciColorHelper.ShowSelectColor(this, _hatchColor, byLayer: false, byBlock: false, result =>
        {
            if (!result.ByLayer && !result.ByBlock)
            {
                _hatchColor = result.Aci;
                UpdateHatchSwatch();
            }
        });
    }

    private void OnGridCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel)
        {
            return;
        }

        // Defer until the edit transaction is fully closed — Refresh/redraw during
        // CellEditEnding routinely crashes AutoCAD (FATAL e0434352).
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                NormalizeLaneRoles(refreshGrids: false);
                DrawPreview();
            }
            catch
            {
                // Never let preview/binding side-effects abort AutoCAD.
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnPointsEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // sync later on OK / type switch
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_drawingPreview || e.NewSize.Width < 40 || e.NewSize.Height < 40)
        {
            return;
        }

        DrawPreview();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        PushPointsIntoWorkingType();
        if (!TryValidateLanes(out var error))
        {
            ShowWarning(error);
            return;
        }

        SaveWorkingType(_activeName);
        Result = BuildResult();
        CloseAction = LaneWidthCloseAction.Confirmed;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseAction = LaneWidthCloseAction.Cancelled;
        DialogResult = false;
        Close();
    }

    private void CloseForPick(LaneWidthCloseAction action)
    {
        CommitGrids();
        PushPointsIntoWorkingType();
        SaveWorkingType(_activeName);
        Result = BuildResult();
        CloseAction = action;
        DialogResult = false;
        Close();
    }

    private void DeleteSelected(DataGrid grid, ObservableCollection<LaneWidthLane> collection)
    {
        if (grid.SelectedItem is LaneWidthLane selected)
        {
            collection.Remove(selected);
            RebuildPointRows();
            DrawPreview();
        }
    }

    private void MoveSelected(
        DataGrid grid,
        ObservableCollection<LaneWidthLane> collection,
        int delta)
    {
        CommitGrids();
        var index = grid.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= collection.Count)
        {
            return;
        }

        collection.Move(index, target);
        grid.SelectedIndex = target;
        DrawPreview();
    }

    private void LoadWorkingType(LaneWidthType type)
    {
        _left.Clear();
        _right.Clear();
        foreach (var lane in type.Left)
        {
            _left.Add(lane.Clone());
        }

        foreach (var lane in type.Right)
        {
            _right.Add(lane.Clone());
        }
    }

    private void SaveWorkingType(string name)
    {
        _types[name] = new LaneWidthType
        {
            Name = name,
            Left = _left.Select(lane => lane.Clone()).ToList(),
            Right = _right.Select(lane => lane.Clone()).ToList()
        };
    }

    private void RebuildPointRows()
    {
        _points.Clear();
        foreach (var lane in _left.Concat(_right))
        {
            foreach (var point in lane.WidthPoints)
            {
                _points.Add(new LaneWidthPointRow
                {
                    Station = point.Station,
                    LaneId = lane.Id,
                    Label = lane.Label,
                    Width = point.Width
                });
            }
        }
    }

    private void PushPointsIntoWorkingType()
    {
        foreach (var lane in _left.Concat(_right))
        {
            lane.WidthPoints.Clear();
        }

        foreach (var row in _points)
        {
            var lane = _left.Concat(_right).FirstOrDefault(item =>
                string.Equals(item.Id, row.LaneId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Label, row.Label, StringComparison.OrdinalIgnoreCase));
            if (lane is null)
            {
                continue;
            }

            lane.WidthPoints.Add(new LaneWidthPoint
            {
                Station = row.Station,
                Width = row.Width
            });
            if (!string.IsNullOrWhiteSpace(row.Label))
            {
                lane.Label = row.Label.Trim();
            }
        }
    }

    private LaneWidthDefinitionSet BuildResult()
    {
        ParseSettings(out var widening, out var hatch, out var drawBoundaries);
        return new LaneWidthDefinitionSet
        {
            ActiveTypeName = _activeName,
            Types = _types.Values
                .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(type => type.Clone())
                .ToList(),
            Assignments = _assignments.Select(item => item.Clone()).ToList(),
            Widening = widening,
            Hatch = hatch,
            DrawBoundaries = drawBoundaries
        };
    }

    private void ParseSettings(
        out LaneWideningSettings widening,
        out LaneHatchSettings hatch,
        out bool drawBoundaries)
    {
        widening = new LaneWideningSettings
        {
            Enabled = WideningEnabledBox.IsChecked == true,
            DesignSpeedKmh = ParseDouble(SpeedBox.Text, 60),
            ManualDeltaLeft = ParseDouble(ManualLeftBox.Text, 0),
            ManualDeltaRight = ParseDouble(ManualRightBox.Text, 0),
            TransitionLength = ParseDouble(TransitionBox.Text, 20)
        };
        hatch = new LaneHatchSettings
        {
            Enabled = HatchEnabledBox.IsChecked == true,
            Pattern = (HatchPatternBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ANSI31",
            Scale = ParseDouble(HatchScaleBox.Text, 1),
            Angle = ParseDouble(HatchAngleBox.Text, 0),
            ColorIndex = _hatchColor
        };
        drawBoundaries = DrawBoundariesBox.IsChecked == true;
    }

    private void RefreshTypeBox()
    {
        _changingType = true;
        try
        {
            TypeBox.Items.Clear();
            foreach (var name in _types.Keys.OrderBy(value => value))
            {
                TypeBox.Items.Add(name);
            }

            TypeBox.SelectedItem = _activeName;
            TypeBox.Text = _activeName;
        }
        finally
        {
            _changingType = false;
        }
    }

    private bool TryValidateLanes(out string error)
    {
        error = string.Empty;
        if (_left.Count == 0 && _right.Count == 0)
        {
            error = "Dodajte najmanje jednu levu ili desnu traku.";
            return false;
        }

        foreach (var lane in _left.Concat(_right))
        {
            if (string.IsNullOrWhiteSpace(lane.Label))
            {
                error = "Svaka traka mora imati oznaku.";
                return false;
            }

            if (lane.Width <= 0 || lane.Width > 100)
            {
                error = "Širina svake trake mora biti u opsegu (0, 100] m.";
                return false;
            }
        }

        return true;
    }

    private void CommitGrids()
    {
        LeftGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LeftGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RightGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RightGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PointsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        AssignmentsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        AssignmentsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        NormalizeLaneRoles(refreshGrids: false);
    }

    private void NormalizeLaneRoles(bool refreshGrids)
    {
        foreach (var lane in _left.Concat(_right))
        {
            var label = lane.Label ?? string.Empty;
            if (label.IndexOf("BANKINA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                label.IndexOf("SHOULDER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                label.IndexOf("BABKINA", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                lane.Role = LaneRole.Shoulder;
            }
            else if (label.IndexOf("RAZDELNA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     label.IndexOf("MEDIAN", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                lane.Role = LaneRole.Other;
            }
        }

        // Items.Refresh during/after cell edit can FATAL AutoCAD — only refresh when safe.
        if (!refreshGrids)
        {
            return;
        }

        try
        {
            LeftGrid.Items.Refresh();
            RightGrid.Items.Refresh();
        }
        catch
        {
            // ignore
        }
    }

    private void DrawPreview()
    {
        if (_drawingPreview || !IsLoaded ||
            PreviewCanvas.ActualWidth < 40 || PreviewCanvas.ActualHeight < 40)
        {
            return;
        }

        _drawingPreview = true;
        try
        {
            PreviewCanvas.Children.Clear();
            var width = PreviewCanvas.ActualWidth;
            var height = PreviewCanvas.ActualHeight;
            var roadY = height * 0.48;
            var leftTotal = _left.Sum(lane => SafeWidth(lane.Width));
            var rightTotal = _right.Sum(lane => SafeWidth(lane.Width));
            var total = leftTotal + rightTotal;
            var scale = total > 1e-6 ? width * 0.72 / total : 24.0;
            var centerX = total > 1e-6 ? width * 0.14 + leftTotal * scale : width * 0.5;

            PreviewCanvas.Children.Add(new Line
            {
                X1 = centerX,
                Y1 = Math.Max(12, roadY - height * 0.28),
                X2 = centerX,
                Y2 = Math.Min(height - 10, roadY + height * 0.28),
                Stroke = AxisBrush,
                StrokeThickness = 1.4,
                StrokeDashArray = [4, 3]
            });
            DrawSide(_left, centerX, roadY, scale, -1, LeftBrush);
            DrawSide(_right, centerX, roadY, scale, 1, RightBrush);
        }
        catch
        {
            // Preview must never take down AutoCAD.
        }
        finally
        {
            _drawingPreview = false;
        }
    }

    private void DrawSide(
        IReadOnlyList<LaneWidthLane> lanes,
        double centerX,
        double roadY,
        double scale,
        int direction,
        Brush brush)
    {
        var currentX = centerX;
        foreach (var lane in lanes)
        {
            var laneWidth = SafeWidth(lane.Width);
            var nextX = currentX + direction * laneWidth * scale;
            var minX = Math.Min(currentX, nextX);
            var maxX = Math.Max(currentX, nextX);
            var stroke = lane.IsCarriageway ? brush : DimBrush;
            PreviewCanvas.Children.Add(new Line
            {
                X1 = currentX,
                Y1 = roadY,
                X2 = nextX,
                Y2 = roadY,
                Stroke = stroke,
                StrokeThickness = 3.2
            });
            var block = new TextBlock
            {
                Text = $"{lane.Label}\n{laneWidth:0.00}",
                Width = Math.Max(24, maxX - minX),
                TextAlignment = TextAlignment.Center,
                Foreground = LabelBrush,
                FontSize = 10
            };
            Canvas.SetLeft(block, minX);
            Canvas.SetTop(block, roadY - 34);
            PreviewCanvas.Children.Add(block);
            currentX = nextX;
        }
    }

    private void SelectHatchPattern(string pattern)
    {
        foreach (var item in HatchPatternBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), pattern, StringComparison.OrdinalIgnoreCase))
            {
                HatchPatternBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateHatchSwatch() =>
        HatchColorSwatch.Background = AciColorHelper.ToBrush(_hatchColor);

    private static string FlipLabel(string label, string to, string from)
    {
        var tokenFrom = "_" + from;
        var tokenTo = "_" + to;
        var index = label.IndexOf(tokenFrom, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return label;
        }

        return label.Substring(0, index) + tokenTo + label.Substring(index + tokenFrom.Length);
    }

    private static LaneWidthLane NewLane(string id, string label) => new()
    {
        Id = id,
        Label = label,
        Width = 3.5,
        Role = LaneRole.Carriageway
    };

    private static double SafeWidth(double width) =>
        double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ? 0 : width;

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
