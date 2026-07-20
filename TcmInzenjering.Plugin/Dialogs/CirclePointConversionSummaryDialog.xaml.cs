using System.Globalization;
using System.Windows;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class CirclePointConversionSummaryDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly IReadOnlyList<Point3d> _points;

    public string GroupName { get; private set; }

    /// <summary>True ako je korisnik potvrdio snimanje/preimenovanje grupe.</summary>
    public bool SaveRequested { get; private set; }

    public CirclePointConversionSummaryDialog(
        string sourceLayer,
        string groupName,
        IReadOnlyList<Point3d> points,
        int convertedCircles,
        int newlyAdded)
    {
        _points = points;
        GroupName = groupName;
        InitializeComponent();

        SummaryText.Text =
            $"Lejer uzorka: {sourceLayer}\n" +
            $"Pronadjeno i pretvoreno krugova: {convertedCircles}\n" +
            $"Novih tacaka u skupu terena: {newlyAdded}\n" +
            $"Tacke su formirane iz XYZ centra krugova.";
        GroupNameBox.Text = groupName;
        PointsGrid.ItemsSource = points
            .Select((p, i) => new
            {
                Index = i + 1,
                X = p.X.ToString("0.000", Inv),
                Y = p.Y.ToString("0.000", Inv),
                Z = p.Z.ToString("0.000", Inv)
            })
            .ToList();
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Hidden;
        try
        {
            if (!TerrainPointPreview.ShowPoints(_points, "grupu tacaka", out var error))
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            GroupName = TerrainPointGroupStore.NormalizeName(GroupNameBox.Text);
            SaveRequested = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        SaveRequested = false;
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
