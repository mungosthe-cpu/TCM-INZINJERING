using System.Globalization;
using System.Windows;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class TerrainProjectionDialog : Window
{
    public TerrainSamplingMode SelectedMode { get; private set; } = TerrainSamplingMode.FixedPointCount;

    public int PointCount { get; private set; } = 100;

    public TerrainProjectionDialog(
        double axisLength,
        int edgeCrossingCount,
        int structureStationCount,
        int defaultPointCount = 100,
        Window? owner = null)
    {
        PointCount = Math.Max(2, defaultPointCount);

        if (owner is not null)
        {
            Owner = owner;
        }

        InitializeComponent();

        InfoText.Text =
            $"Dužina ose: {axisLength:0.##} m. " +
            $"Pronađeno {edgeCrossingCount} preseka sa linijama terena (3DFACE/TIN ivice).";

        EdgeDetailRun.Text = edgeCrossingCount > 0
            ? $"(+ {edgeCrossingCount} preseka; PC/PT; preciznost ispod)"
            : "(nema preseka — koristi samo preciznost / broj tačaka)";

        PointCountBox.Text = PointCount.ToString(CultureInfo.InvariantCulture);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PointCountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count < 2)
        {
            MessageBox.Show(
                this,
                "Broj tačaka 3D polilinije mora biti ceo broj ≥ 2.",
                "TCM-INŽINJERING",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        PointCount = count;
        SelectedMode = EdgeCrossingsRadio.IsChecked == true
            ? TerrainSamplingMode.TerrainEdgeCrossings
            : TerrainSamplingMode.FixedPointCount;

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public TerrainSamplingOptions ToOptions() => new()
    {
        Mode = SelectedMode,
        PointCount = PointCount
    };
}
