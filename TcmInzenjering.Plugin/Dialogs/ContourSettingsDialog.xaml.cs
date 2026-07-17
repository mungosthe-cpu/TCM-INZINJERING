using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TcmInzenjering.Plugin.Roads;
using TcmInzenjering.Plugin.Roads.Terrain;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class ContourSettingsDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] DisplayModes =
    [
        "Use Surface Elevation",
        "Flatten to Elevation",
        "Exaggerate Elevation"
    ];

    private readonly ObservableCollection<DisplayRowVm> _displayRows = new();
    private readonly DispatcherTimer _liveApplyTimer;
    private short _dataPointAci = 1;
    private short _derivedPointAci = 7;
    private short _contourLabelAci = 1;
    private bool _loading;
    private bool _applying;
    private string? _lastAppliedFingerprint;

    public ContourSettingsDialog(Window? owner = null)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        _liveApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _liveApplyTimer.Tick += (_, _) =>
        {
            _liveApplyTimer.Stop();
            PersistAndApplyToDrawing(closeAfter: false, quiet: true);
        };

        ContourPreferences.Load();
        FillModeBoxes();
        LoadFrom(ContourPreferences.Current.Clone());
        HookLiveApply();
        Tabs.SelectionChanged += (_, _) =>
        {
            if (Tabs.SelectedItem is TabItem { Header: "Summary" })
            {
                RefreshSummary();
            }
        };
    }

    private void HookLiveApply()
    {
        foreach (var box in new[]
                 {
                     BaseBox, MinorBox, MajorBox, UserContoursBox, ContourFlattenBox, ContourExagBox,
                     DepressionLenBox, RangeCountBox, ContourLabelHeightBox, ContourLabelDecimalsBox
                 })
        {
            box.LostFocus += (_, _) => ScheduleLiveApply();
        }

        ContourLabelFontBox.LostFocus += (_, _) => ScheduleLiveApply();
        ContourLabelFontBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        ContourLabelMaskBox.Checked += (_, _) => ScheduleLiveApply();
        ContourLabelMaskBox.Unchecked += (_, _) => ScheduleLiveApply();

        SmoothBox.Checked += (_, _) => ScheduleLiveApply();
        SmoothBox.Unchecked += (_, _) => ScheduleLiveApply();
        DepressionBox.Checked += (_, _) => ScheduleLiveApply();
        DepressionBox.Unchecked += (_, _) => ScheduleLiveApply();
        SmoothTypeBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        ContourModeBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        RangeGroupBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        MajorDisplayLtBox.LostFocus += (_, _) => ScheduleLiveApply();
        MinorDisplayLtBox.LostFocus += (_, _) => ScheduleLiveApply();
        MajorDisplayLtBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        MinorDisplayLtBox.SelectionChanged += (_, _) => ScheduleLiveApply();

        SmoothFactorSlider.PreviewMouseUp += (_, _) => ScheduleLiveApply();
        SmoothFactorSlider.LostMouseCapture += (_, _) => ScheduleLiveApply();
        SmoothFactorSlider.KeyUp += (_, _) => ScheduleLiveApply();

        // Display / Analysis — live osvežavanje. Analysis checkbox ažurira Display Visible.
        foreach (var box in new[] { ExtBorderBox, IntBorderBox, LegendBox })
        {
            box.Checked += (_, _) => ScheduleLiveApply();
            box.Unchecked += (_, _) => ScheduleLiveApply();
        }

        WireAnalysisToDisplay(ShowWatershedsBox, "Watersheds");
        WireAnalysisToDisplay(AnalyzeDirBox, "Directions");
        WireAnalysisToDisplay(AnalyzeElevBox, "Elevations");
        WireAnalysisToDisplay(AnalyzeSlopeBox, "Slopes");
        WireAnalysisToDisplay(AnalyzeArrowBox, "Slope Arrows");

        BorderModeBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        TriangleModeBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        GridModeBox.SelectionChanged += (_, _) => ScheduleLiveApply();
        PointModeBox.SelectionChanged += (_, _) => ScheduleLiveApply();
    }

    private void WireAnalysisToDisplay(System.Windows.Controls.CheckBox box, string componentType)
    {
        box.Checked += (_, _) =>
        {
            SyncDisplayRowVisible(componentType, true);
            ScheduleLiveApply();
        };
        box.Unchecked += (_, _) =>
        {
            SyncDisplayRowVisible(componentType, false);
            ScheduleLiveApply();
        };
    }

    private void ScheduleLiveApply()
    {
        if (_loading || _applying)
        {
            return;
        }

        _liveApplyTimer.Stop();
        _liveApplyTimer.Start();
    }

    private void PersistAndApplyToDrawing(bool closeAfter, bool quiet)
    {
        if (_applying)
        {
            return;
        }

        _applying = true;
        _liveApplyTimer.Stop();
        try
        {
            if (!TryBuildSnapshot(out var snap, out var error))
            {
                if (!quiet)
                {
                    MessageBox.Show(this, error, "TCM-INŽINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            var fingerprint = StyleFingerprint(snap);
            if (quiet &&
                string.Equals(fingerprint, _lastAppliedFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            ContourPreferences.Save(snap);
            _lastAppliedFingerprint = StyleFingerprint(ContourPreferences.Current);
            ModifiedByLbl.Text = ContourPreferences.Current.LastModifiedBy;
            DateModifiedLbl.Text = FormatDate(ContourPreferences.Current.DateModified);
            // Live Display: ne crta nove izohipse ako nisu već u crtežu; Apply/OK uvek primeni stil.
            RoadCommands.ApplyCurrentContourStyleToDrawing(
                writeMessage: !quiet,
                contoursOnlyIfPresent: quiet);

            if (closeAfter)
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
        }
        finally
        {
            _applying = false;
        }
    }

    private static string StyleFingerprint(SurfaceStyleSnapshot snap)
    {
        // Normalizuj kao Save (npr. Layer "0" → TCM_IZO_*), pa izbaci metapodatke datuma.
        var clone = snap.Clone().Normalized();
        clone.DateCreated = "";
        clone.DateModified = "";
        clone.LastModifiedBy = "";
        clone.CreatedBy = "";
        return System.Text.Json.JsonSerializer.Serialize(clone);
    }

    private void FillModeBoxes()
    {
        foreach (var box in new[]
                 {
                     BorderModeBox, ContourModeBox, GridModeBox, PointModeBox, TriangleModeBox
                 })
        {
            box.Items.Clear();
            foreach (var m in DisplayModes)
            {
                box.Items.Add(m);
            }
        }
    }

    private void LoadFrom(SurfaceStyleSnapshot s)
    {
        _loading = true;
        StyleNameBox.Text = s.StyleName;
        DescBox.Text = s.Description;
        CreatedByLbl.Text = s.CreatedBy;
        ModifiedByLbl.Text = s.LastModifiedBy;
        DateCreatedLbl.Text = FormatDate(s.DateCreated);
        DateModifiedLbl.Text = FormatDate(s.DateModified);

        BorderModeBox.SelectedIndex = (int)s.BorderDisplayMode;
        BorderFlattenBox.Text = F(s.FlattenBordersElevation);
        BorderExagBox.Text = F(s.ExaggerateBordersScale);
        ExtBorderBox.IsChecked = s.DisplayExteriorBorders;
        IntBorderBox.IsChecked = s.DisplayInteriorBorders;
        UseDatumBox.IsChecked = s.UseDatum;
        ProjectDatumBox.IsChecked = s.ProjectGridToDatum;
        DatumElevBox.Text = F(s.DatumElevation);
        OnBorderModeChanged(null!, null!);
        OnDatumChanged(null!, null!);

        RangeGroupBox.SelectedIndex = (int)s.ContourRangeGroupBy;
        RangeCountBox.Text = s.ContourRangeCount.ToString(Inv);
        ContourModeBox.SelectedIndex = (int)s.ContourDisplayMode;
        ContourFlattenBox.Text = F(s.FlattenContoursElevation);
        ContourExagBox.Text = F(s.ExaggerateContoursScale);
        LegendBox.IsChecked = s.ShowContourLegend;
        BaseBox.Text = F(s.BaseElevation);
        MinorBox.Text = F(s.MinorInterval);
        MajorBox.Text = F(s.MajorInterval);
        UserContoursBox.Text = s.UserContoursText;
        DepressionBox.IsChecked = s.DisplayDepressionTicks;
        DepressionLenBox.Text = F(s.DepressionTickLength);
        SmoothBox.IsChecked = s.SmoothContours;
        SmoothTypeBox.SelectedIndex = s.SmoothType == ContourSmoothType.SplineCurve ? 1 : 0;
        SmoothFactorSlider.Value = s.SmoothFactor;
        MajorDisplayLtBox.Text = s.MajorDisplayLinetype;
        MinorDisplayLtBox.Text = s.MinorDisplayLinetype;

        var fonts = StationFontCatalog.Load();
        ContourLabelFontBox.ItemsSource = fonts;
        ContourLabelFontBox.DisplayMemberPath = nameof(StationFontOption.DisplayName);
        ContourLabelFontBox.SelectedValuePath = nameof(StationFontOption.FileName);
        var labelFont = fonts.FirstOrDefault(f =>
            string.Equals(f.FileName, s.ContourLabelFont, StringComparison.OrdinalIgnoreCase));
        ContourLabelFontBox.SelectedItem = labelFont;
        ContourLabelFontBox.Text = labelFont?.DisplayName ?? s.ContourLabelFont;
        ContourLabelHeightBox.Text = F(s.ContourLabelHeight);
        ContourLabelDecimalsBox.Text = s.ContourLabelDecimals.ToString(Inv);
        _contourLabelAci = s.ContourLabelColorAci;
        AciColorHelper.ApplyToButton(ContourLabelColorBtn, _contourLabelAci);
        ContourLabelMaskBox.IsChecked = s.ContourLabelBackgroundMask;

        OnContourModeChanged(null!, null!);
        OnDepressionChanged(null!, null!);
        UpdateSmoothUi();

        GridModeBox.SelectedIndex = (int)s.GridDisplayMode;
        GridFlattenBox.Text = F(s.FlattenGridElevation);
        GridExagBox.Text = F(s.ExaggerateGridScale);
        PrimaryGridBox.IsChecked = s.UsePrimaryGrid;
        PrimaryIntBox.Text = F(s.PrimaryGridInterval);
        PrimaryOriBox.Text = F(s.PrimaryGridOrientationDeg);
        SecondaryGridBox.IsChecked = s.UseSecondaryGrid;
        SecondaryIntBox.Text = F(s.SecondaryGridInterval);
        SecondaryOriBox.Text = F(s.SecondaryGridOrientationDeg);
        OnGridModeChanged(null!, null!);

        PointModeBox.SelectedIndex = (int)s.PointDisplayMode;
        PointFlattenBox.Text = F(s.FlattenPointsElevation);
        PointExagBox.Text = F(s.ExaggeratePointsScale);
        PointScaleMethodBox.Text = s.PointScalingMethod;
        PointUnitsBox.Text = F(s.PointUnits);
        DataSymBox.Text = s.DataPointSymbol.ToString(Inv);
        DerivedSymBox.Text = s.DerivedPointSymbol.ToString(Inv);
        _dataPointAci = s.DataPointColorAci;
        _derivedPointAci = s.DerivedPointColorAci;
        DataColorByLayerBox.IsChecked = s.DataPointColorByLayer;
        DerivedColorByLayerBox.IsChecked = s.DerivedPointColorByLayer;
        AciColorHelper.ApplyToButton(DataColorBtn, _dataPointAci);
        AciColorHelper.ApplyToButton(DerivedColorBtn, _derivedPointAci);
        OnPointModeChanged(null!, null!);

        TriangleModeBox.SelectedIndex = (int)s.TriangleDisplayMode;
        TriangleFlattenBox.Text = F(s.FlattenTrianglesElevation);
        TriangleExagBox.Text = F(s.ExaggerateTrianglesScale);
        OnTriangleModeChanged(null!, null!);

        ShowWatershedsBox.IsChecked = s.ShowWatersheds;
        AnalyzeDirBox.IsChecked = s.AnalyzeDirections;
        AnalyzeElevBox.IsChecked = s.AnalyzeElevations;
        AnalyzeSlopeBox.IsChecked = s.AnalyzeSlopes;
        AnalyzeArrowBox.IsChecked = s.AnalyzeSlopeArrows;

        ViewDirBox.SelectedIndex = string.Equals(s.ViewDirection, "Model", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _displayRows.Clear();
        foreach (var c in s.Components)
        {
            var row = DisplayRowVm.From(c);
            row.PropertyChanged += (_, _) => ScheduleLiveApply();
            _displayRows.Add(row);
        }

        DisplayGrid.ItemsSource = _displayRows;
        _loading = false;
    }

    private bool TryBuildSnapshot(out SurfaceStyleSnapshot snapshot, out string error)
    {
        snapshot = ContourPreferences.Current.Clone();
        error = "";

        if (!TryParse(BaseBox.Text, out var baseElev) ||
            !TryParse(MinorBox.Text, out var minor) ||
            !TryParse(MajorBox.Text, out var major) ||
            !TryParse(RangeCountBox.Text, out var rangeCount) ||
            !TryParse(BorderFlattenBox.Text, out var borderFlat) ||
            !TryParse(BorderExagBox.Text, out var borderExag) ||
            !TryParse(DatumElevBox.Text, out var datumElev) ||
            !TryParse(ContourFlattenBox.Text, out var contourFlat) ||
            !TryParse(ContourExagBox.Text, out var contourExag) ||
            !TryParse(DepressionLenBox.Text, out var depLen) ||
            !TryParse(ContourLabelHeightBox.Text, out var labelHeight) ||
            !TryParse(GridFlattenBox.Text, out var gridFlat) ||
            !TryParse(GridExagBox.Text, out var gridExag) ||
            !TryParse(PrimaryIntBox.Text, out var pInt) ||
            !TryParse(PrimaryOriBox.Text, out var pOri) ||
            !TryParse(SecondaryIntBox.Text, out var sInt) ||
            !TryParse(SecondaryOriBox.Text, out var sOri) ||
            !TryParse(PointFlattenBox.Text, out var ptFlat) ||
            !TryParse(PointExagBox.Text, out var ptExag) ||
            !TryParse(PointUnitsBox.Text, out var ptUnits) ||
            !TryParse(TriangleFlattenBox.Text, out var triFlat) ||
            !TryParse(TriangleExagBox.Text, out var triExag))
        {
            error = "Proverite brojcane vrednosti.";
            return false;
        }

        if (minor <= 0 || major <= 0)
        {
            error = "Minor/Major interval moraju biti > 0.";
            return false;
        }

        if (!int.TryParse(DataSymBox.Text.Trim(), NumberStyles.Integer, Inv, out var dataSym) ||
            !int.TryParse(DerivedSymBox.Text.Trim(), NumberStyles.Integer, Inv, out var derSym) ||
            !int.TryParse(ContourLabelDecimalsBox.Text.Trim(), NumberStyles.Integer, Inv, out var labelDecimals))
        {
            error = "Point Symbol / broj decimala moraju biti celi brojevi.";
            return false;
        }

        labelDecimals = MathNet48.Clamp(labelDecimals, 0, 6);
        if (labelHeight < 0.1)
        {
            error = "Visina kotne oznake mora biti ≥ 0.1.";
            return false;
        }

        snapshot.StyleName = (StyleNameBox.Text ?? "Standard").Trim();
        snapshot.Description = DescBox.Text ?? "";
        snapshot.BorderDisplayMode = (SurfaceElevationDisplayMode)MathNet48.Clamp(BorderModeBox.SelectedIndex, 0, 2);
        snapshot.FlattenBordersElevation = borderFlat;
        snapshot.ExaggerateBordersScale = borderExag;
        snapshot.DisplayExteriorBorders = ExtBorderBox.IsChecked == true;
        snapshot.DisplayInteriorBorders = IntBorderBox.IsChecked == true;
        snapshot.UseDatum = UseDatumBox.IsChecked == true;
        snapshot.ProjectGridToDatum = ProjectDatumBox.IsChecked == true;
        snapshot.DatumElevation = datumElev;

        snapshot.ContourRangeGroupBy = (ContourRangeGroupBy)MathNet48.Clamp(RangeGroupBox.SelectedIndex, 0, 2);
        snapshot.ContourRangeCount = Math.Max(1, (int)rangeCount);
        snapshot.ContourDisplayMode = (SurfaceElevationDisplayMode)MathNet48.Clamp(ContourModeBox.SelectedIndex, 0, 2);
        snapshot.FlattenContoursElevation = contourFlat;
        snapshot.ExaggerateContoursScale = contourExag;
        snapshot.ShowContourLegend = LegendBox.IsChecked == true;
        snapshot.BaseElevation = baseElev;
        snapshot.MinorInterval = minor;
        snapshot.MajorInterval = major;
        snapshot.UserContoursText = UserContoursBox.Text ?? "";
        snapshot.DisplayDepressionTicks = DepressionBox.IsChecked == true;
        snapshot.DepressionTickLength = depLen;
        snapshot.SmoothContours = SmoothBox.IsChecked == true;
        snapshot.SmoothType = SmoothTypeBox.SelectedIndex == 1
            ? ContourSmoothType.SplineCurve
            : ContourSmoothType.AddVertices;
        snapshot.SmoothFactor = (int)SmoothFactorSlider.Value;
        snapshot.MajorDisplayLinetype = GetComboText(MajorDisplayLtBox) ?? "Continuous";
        snapshot.MinorDisplayLinetype = GetComboText(MinorDisplayLtBox) ?? "Continuous";

        var selectedFont = ContourLabelFontBox.SelectedItem as StationFontOption;
        snapshot.ContourLabelFont = StationFontCatalog.ResolveFileName(
            selectedFont?.FileName ?? ContourLabelFontBox.Text);
        snapshot.ContourLabelHeight = labelHeight;
        snapshot.ContourLabelDecimals = labelDecimals;
        snapshot.ContourLabelColorAci = _contourLabelAci;
        snapshot.ContourLabelBackgroundMask = ContourLabelMaskBox.IsChecked == true;

        snapshot.GridDisplayMode = (SurfaceElevationDisplayMode)MathNet48.Clamp(GridModeBox.SelectedIndex, 0, 2);
        snapshot.FlattenGridElevation = gridFlat;
        snapshot.ExaggerateGridScale = gridExag;
        snapshot.UsePrimaryGrid = PrimaryGridBox.IsChecked == true;
        snapshot.PrimaryGridInterval = pInt;
        snapshot.PrimaryGridOrientationDeg = pOri;
        snapshot.UseSecondaryGrid = SecondaryGridBox.IsChecked == true;
        snapshot.SecondaryGridInterval = sInt;
        snapshot.SecondaryGridOrientationDeg = sOri;

        snapshot.PointDisplayMode = (SurfaceElevationDisplayMode)MathNet48.Clamp(PointModeBox.SelectedIndex, 0, 2);
        snapshot.FlattenPointsElevation = ptFlat;
        snapshot.ExaggeratePointsScale = ptExag;
        snapshot.PointScalingMethod = PointScaleMethodBox.Text ?? "Size in absolute units";
        snapshot.PointUnits = ptUnits;
        snapshot.DataPointSymbol = dataSym;
        snapshot.DerivedPointSymbol = derSym;
        snapshot.DataPointColorAci = _dataPointAci;
        snapshot.DerivedPointColorAci = _derivedPointAci;
        snapshot.DataPointColorByLayer = DataColorByLayerBox.IsChecked == true;
        snapshot.DerivedPointColorByLayer = DerivedColorByLayerBox.IsChecked == true;

        snapshot.TriangleDisplayMode = (SurfaceElevationDisplayMode)MathNet48.Clamp(TriangleModeBox.SelectedIndex, 0, 2);
        snapshot.FlattenTrianglesElevation = triFlat;
        snapshot.ExaggerateTrianglesScale = triExag;

        snapshot.ViewDirection = ViewDirBox.SelectedIndex == 1 ? "Model" : "Plan";
        snapshot.Components = _displayRows.Select(r => r.ToStyle()).ToList();
        // Contours tab linetype → snapshot components (NE diraj UI redove — sprečava live-apply petlju).
        snapshot.GetComponent("Major Contour").Linetype = snapshot.MajorDisplayLinetype;
        snapshot.GetComponent("Minor Contour").Linetype = snapshot.MinorDisplayLinetype;

        // Display Visible = izvor istine; Analysis flagovi prate.
        snapshot.AnalyzeSlopes = snapshot.GetComponent("Slopes").Visible;
        snapshot.AnalyzeElevations = snapshot.GetComponent("Elevations").Visible;
        snapshot.AnalyzeSlopeArrows = snapshot.GetComponent("Slope Arrows").Visible;
        snapshot.AnalyzeDirections = snapshot.GetComponent("Directions").Visible;
        snapshot.ShowWatersheds = snapshot.GetComponent("Watersheds").Visible;

        return true;
    }

    private void SyncDisplayRowVisible(string componentType, bool visible)
    {
        var row = _displayRows.FirstOrDefault(r =>
            string.Equals(r.ComponentType, componentType, StringComparison.OrdinalIgnoreCase));
        if (row is not null && row.Visible != visible)
        {
            row.Visible = visible;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _liveApplyTimer.Stop();
        PersistAndApplyToDrawing(closeAfter: true, quiet: false);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        _liveApplyTimer.Stop();
        PersistAndApplyToDrawing(closeAfter: false, quiet: false);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _liveApplyTimer.Stop();
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnHelp(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Stil terena prati Civil 3D Surface Style.\n\n" +
            "• Contours — intervali, smooth, depression, user contours\n" +
            "• Display — Visible / Layer / Color / Linetype / Lineweight\n" +
            "• Ostali tabovi — podešavanja za budući prikaz (Borders, Grid, Points…)\n\n" +
            "Crtanje: TCMTERIZO (Izohipse → Crtaj izohipse).",
            "Help — Stil terena",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void OnRefreshSummary(object sender, RoutedEventArgs e) => RefreshSummary();

    private void RefreshSummary()
    {
        if (!TryBuildSnapshot(out var s, out _))
        {
            SummaryBox.Text = "(Ispravite vrednosti pa osvežite Summary.)";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Information / Name: {s.StyleName}");
        sb.AppendLine($"Contour Intervals: base={s.BaseElevation:0.###} minor={s.MinorInterval:0.###} major={s.MajorInterval:0.###}");
        sb.AppendLine($"Contour Smoothing: {(s.SmoothContours ? "On" : "Off")} / {s.SmoothType} / {s.SmoothFactor}");
        sb.AppendLine($"Depressions: {(s.DisplayDepressionTicks ? "On" : "Off")} len={s.DepressionTickLength:0.###}");
        sb.AppendLine($"Borders: exterior={s.DisplayExteriorBorders} interior={s.DisplayInteriorBorders}");
        sb.AppendLine($"Grid: primary={s.UsePrimaryGrid}/{s.PrimaryGridInterval:0.###} secondary={s.UseSecondaryGrid}/{s.SecondaryGridInterval:0.###}");
        sb.AppendLine($"Points: units={s.PointUnits:0.###} scale={s.PointScalingMethod}");
        sb.AppendLine($"Triangles mode: {s.TriangleDisplayMode}");
        sb.AppendLine($"Analysis: dir={s.AnalyzeDirections} elev={s.AnalyzeElevations} slope={s.AnalyzeSlopes} arrows={s.AnalyzeSlopeArrows}");
        sb.AppendLine($"View: {s.ViewDirection}");
        sb.AppendLine("Display:");
        foreach (var c in s.Components)
        {
            var color = AciColorHelper.ToDisplayName(c.ColorAci, c.ColorByLayer, c.ColorByBlock);
            var lw = c.LineWeightByBlock ? "ByBlock" : $"{c.LineWeightMm:0.##} mm";
            sb.AppendLine($"  {(c.Visible ? "[ON] " : "[off]")}{c.ComponentType,-16} L={c.Layer} C={color} LT={c.Linetype} LW={lw}");
        }

        SummaryBox.Text = sb.ToString();
    }

    private void OnDisplayColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DisplayRowVm row } btn)
        {
            return;
        }

        AciColorHelper.ShowSelectColor(btn, row.ColorAci, row.ColorByLayer, row.ColorByBlock, result =>
        {
            row.ColorByLayer = result.ByLayer;
            row.ColorByBlock = result.ByBlock;
            if (!result.ByLayer && !result.ByBlock)
            {
                row.ColorAci = result.Aci;
            }

            row.NotifyColor();
            ScheduleLiveApply();
        });
    }

    private void OnPickDataColor(object sender, RoutedEventArgs e) =>
        AciColorHelper.ShowPicker(DataColorBtn, _dataPointAci, aci =>
        {
            _dataPointAci = aci;
            DataColorByLayerBox.IsChecked = false;
            AciColorHelper.ApplyToButton(DataColorBtn, aci);
        });

    private void OnContourLabelColorClick(object sender, RoutedEventArgs e) =>
        AciColorHelper.ShowPicker(ContourLabelColorBtn, _contourLabelAci, aci =>
        {
            _contourLabelAci = aci;
            AciColorHelper.ApplyToButton(ContourLabelColorBtn, aci);
            ScheduleLiveApply();
        });

    private void OnPickDerivedColor(object sender, RoutedEventArgs e) =>
        AciColorHelper.ShowPicker(DerivedColorBtn, _derivedPointAci, aci =>
        {
            _derivedPointAci = aci;
            DerivedColorByLayerBox.IsChecked = false;
            AciColorHelper.ApplyToButton(DerivedColorBtn, aci);
        });

    private void OnSmoothChanged(object sender, RoutedEventArgs e) => UpdateSmoothUi();

    private void OnSmoothFactorChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SmoothFactorLbl is not null)
        {
            SmoothFactorLbl.Text = ((int)SmoothFactorSlider.Value).ToString(Inv);
        }
    }

    private void UpdateSmoothUi()
    {
        if (SmoothPanel is null || SmoothBox is null || SmoothFactorLbl is null)
        {
            return;
        }

        SmoothPanel.IsEnabled = SmoothBox.IsChecked == true;
        SmoothFactorLbl.Text = ((int)SmoothFactorSlider.Value).ToString(Inv);
    }

    private void OnBorderModeChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyElevationModeEnable(BorderModeBox, BorderFlattenBox, BorderExagBox);

    private void OnContourModeChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyElevationModeEnable(ContourModeBox, ContourFlattenBox, ContourExagBox);

    private void OnGridModeChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyElevationModeEnable(GridModeBox, GridFlattenBox, GridExagBox);

    private void OnPointModeChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyElevationModeEnable(PointModeBox, PointFlattenBox, PointExagBox);

    private void OnTriangleModeChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyElevationModeEnable(TriangleModeBox, TriangleFlattenBox, TriangleExagBox);

    private void OnDatumChanged(object sender, RoutedEventArgs e)
    {
        if (ProjectDatumBox is null || DatumElevBox is null || UseDatumBox is null)
        {
            return;
        }

        var on = UseDatumBox.IsChecked == true;
        ProjectDatumBox.IsEnabled = on;
        DatumElevBox.IsEnabled = on;
    }

    private void OnDepressionChanged(object sender, RoutedEventArgs e)
    {
        if (DepressionLenBox is null || DepressionBox is null)
        {
            return;
        }

        DepressionLenBox.IsEnabled = DepressionBox.IsChecked == true;
    }

    private static void ApplyElevationModeEnable(ComboBox mode, TextBox flatten, TextBox exag)
    {
        if (mode is null || flatten is null || exag is null)
        {
            return;
        }

        var idx = mode.SelectedIndex;
        flatten.IsEnabled = idx == 1;
        exag.IsEnabled = idx == 2;
    }

    private static string? GetComboText(ComboBox box) =>
        box.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString()
            : box.Text;

    private static string F(double v) => v.ToString("0.###", Inv);

    private static string FormatDate(string? iso)
    {
        if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ||
            DateTime.TryParse(iso, out dt))
        {
            return dt.ToString("G");
        }

        return iso ?? "";
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float, Inv, out value);

    private sealed class DisplayRowVm : INotifyPropertyChanged
    {
        private bool _visible;
        private string _layer = "0";
        private short _colorAci = 7;
        private bool _colorByLayer;
        private bool _colorByBlock;
        private string _linetype = "Continuous";
        private double _ltScale = 1;
        private double _lineWeightMm;
        private bool _lineWeightByBlock = true;

        public string ComponentType { get; init; } = "";

        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value)
                {
                    return;
                }

                _visible = value;
                OnChanged();
            }
        }

        public string Layer
        {
            get => _layer;
            set
            {
                var v = value ?? "0";
                if (_layer == v)
                {
                    return;
                }

                _layer = v;
                OnChanged();
            }
        }

        public short ColorAci
        {
            get => _colorAci;
            set
            {
                if (_colorAci == value)
                {
                    return;
                }

                _colorAci = value;
                NotifyColor();
            }
        }

        public bool ColorByLayer
        {
            get => _colorByLayer;
            set
            {
                if (_colorByLayer == value)
                {
                    return;
                }

                _colorByLayer = value;
                if (value)
                {
                    _colorByBlock = false;
                }

                NotifyColor();
            }
        }

        public bool ColorByBlock
        {
            get => _colorByBlock;
            set
            {
                if (_colorByBlock == value)
                {
                    return;
                }

                _colorByBlock = value;
                if (value)
                {
                    _colorByLayer = false;
                }

                NotifyColor();
            }
        }

        public string ColorText => AciColorHelper.ToDisplayName(ColorAci, ColorByLayer, ColorByBlock);

        public Brush ColorBrush => AciColorHelper.ToDisplayBrush(ColorAci, ColorByLayer, ColorByBlock);

        public string Linetype
        {
            get => _linetype;
            set
            {
                var v = value ?? "Continuous";
                if (_linetype == v)
                {
                    return;
                }

                _linetype = v;
                OnChanged();
            }
        }

        public double LtScale
        {
            get => _ltScale;
            set
            {
                if (Math.Abs(_ltScale - value) < 1e-12)
                {
                    return;
                }

                _ltScale = value;
                OnChanged();
            }
        }

        public string LineWeightText
        {
            get => LineWeightByBlock ? "ByBlock" : LineWeightMm.ToString("0.##", Inv);
            set
            {
                var t = (value ?? "").Trim();
                var prev = LineWeightText;
                if (t.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(t))
                {
                    LineWeightByBlock = true;
                    LineWeightMm = 0;
                }
                else if (TryParse(ReplaceIgnoreCase(t, "mm", ""), out var mm))
                {
                    LineWeightByBlock = false;
                    LineWeightMm = mm;
                }

                if (!string.Equals(prev, LineWeightText, StringComparison.Ordinal))
                {
                    OnChanged(nameof(LineWeightText));
                }
            }
        }

        private static string ReplaceIgnoreCase(string input, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
            {
                return input;
            }

            var idx = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return input;
            }

            return input.Substring(0, idx) + newValue + input.Substring(idx + oldValue.Length);
        }

        public double LineWeightMm
        {
            get => _lineWeightMm;
            set
            {
                if (Math.Abs(_lineWeightMm - value) < 1e-12)
                {
                    return;
                }

                _lineWeightMm = value;
                OnChanged(nameof(LineWeightText));
            }
        }

        public bool LineWeightByBlock
        {
            get => _lineWeightByBlock;
            set
            {
                if (_lineWeightByBlock == value)
                {
                    return;
                }

                _lineWeightByBlock = value;
                OnChanged(nameof(LineWeightText));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyColor()
        {
            OnChanged(nameof(ColorText));
            OnChanged(nameof(ColorBrush));
            OnChanged(nameof(ColorByLayer));
            OnChanged(nameof(ColorByBlock));
        }

        public static DisplayRowVm From(SurfaceComponentStyle c) =>
            new()
            {
                ComponentType = c.ComponentType,
                Visible = c.Visible,
                Layer = c.Layer,
                ColorAci = c.ColorAci,
                ColorByLayer = c.ColorByLayer,
                ColorByBlock = c.ColorByBlock,
                Linetype = c.Linetype,
                LtScale = c.LtScale,
                LineWeightMm = c.LineWeightMm,
                LineWeightByBlock = c.LineWeightByBlock
            };

        public SurfaceComponentStyle ToStyle() =>
            new()
            {
                ComponentType = ComponentType,
                Visible = Visible,
                Layer = string.IsNullOrWhiteSpace(Layer) ? "0" : Layer.Trim(),
                ColorAci = ColorAci,
                ColorByLayer = ColorByLayer,
                ColorByBlock = ColorByBlock,
                Linetype = string.IsNullOrWhiteSpace(Linetype) ? "Continuous" : Linetype.Trim(),
                LtScale = LtScale <= 0 ? 1 : LtScale,
                LineWeightMm = LineWeightMm,
                LineWeightByBlock = LineWeightByBlock
            };

        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
