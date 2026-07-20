using System.Globalization;
using System.Windows;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class BreaklineLayerSummaryDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly IReadOnlyList<Point3d> _addedPoints;
    private readonly IReadOnlyList<(Point3d A, Point3d B)> _failedSegments;
    private readonly Func<string?>? _undo;
    private readonly Func<string?>? _saveToProject;

    public BreaklineLayerSummaryDialog(
        string summary,
        IReadOnlyList<Point3d> addedPoints,
        IReadOnlyList<(Point3d A, Point3d B)> failedSegments,
        Func<string?>? undo,
        Func<string?>? saveToProject = null)
    {
        _addedPoints = addedPoints;
        _failedSegments = failedSegments;
        _undo = undo;
        _saveToProject = saveToProject;
        InitializeComponent();
        SummaryText.Text = summary;

        if (addedPoints.Count > 0)
        {
            AddedHeader.Text = $"Dodate tacke ({addedPoints.Count}):";
            AddedHeader.Visibility = Visibility.Visible;
            AddedGrid.Visibility = Visibility.Visible;
            PreviewBtn.Visibility = Visibility.Visible;
            UndoBtn.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
            AddedGrid.ItemsSource = addedPoints
                .Select((p, i) => new
                {
                    Index = i + 1,
                    X = p.X.ToString("0.000", Inv),
                    Y = p.Y.ToString("0.000", Inv),
                    Z = p.Z.ToString("0.000", Inv)
                })
                .ToList();
        }

        SaveProjectBtn.Visibility = saveToProject is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        PreviewFailedBtn.Visibility = failedSegments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnPreviewPoints(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Hidden;
        try
        {
            if (!TerrainPointPreview.ShowPoints(_addedPoints, "dodate tacke", out var error))
            {
                MessageBox.Show(this, error ?? "Tacke nisu dostupne.",
                    "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            Visibility = Visibility.Visible;
            Activate();
        }
    }

    private void OnPreviewFailed(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Hidden;
        try
        {
            if (!BreaklineSegmentPreview.Show(_failedSegments, out var error))
            {
                MessageBox.Show(this, error ?? "Segmenti nisu dostupni.",
                    "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            Visibility = Visibility.Visible;
            Activate();
        }
    }

    private void OnSaveToProject(object sender, RoutedEventArgs e)
    {
        if (_saveToProject is null)
        {
            return;
        }

        string? result;
        try
        {
            result = _saveToProject();
        }
        catch (Exception ex)
        {
            result = ex.Message;
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            MessageBox.Show(this, "Skup tacaka je snimljen u aktivni TCM projekat.",
                "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this, result, "TCM-ROADS",
            MessageBoxButton.OK,
            result.StartsWith("Nema", StringComparison.OrdinalIgnoreCase)
                ? MessageBoxImage.Warning
                : MessageBoxImage.Information);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo is null ||
            MessageBox.Show(this,
                "Ukloniti automatski dodate tacke, vratiti prethodne forsirane ivice i ponovo izgraditi TIN?",
                "TCM-ROADS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        Visibility = Visibility.Hidden;
        string? error;
        try
        {
            error = _undo();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            Visibility = Visibility.Visible;
            Activate();
        }

        if (error is not null)
        {
            MessageBox.Show(this, error, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UndoBtn.IsEnabled = false;
        PreviewBtn.IsEnabled = false;
        SaveProjectBtn.IsEnabled = false;
        AddedGrid.IsEnabled = false;
        SummaryText.Text += "\n\nAutomatski dodate tacke su ponistene i TIN je vracen.";
        MessageBox.Show(this, "Dodate tacke su uklonjene i TIN je ponovo izgradjen.",
            "TCM-ROADS", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
