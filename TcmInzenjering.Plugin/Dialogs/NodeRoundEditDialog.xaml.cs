using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.Geometry;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public enum NodeRoundCloseAction
{
    Cancel,
    Applied,
    PickRadius,
    PickTangentLeft,
    PickTangentRight
}

public enum ManualCurveMode
{
    Lrl,
    Lr,
    Rl,
    Ll
}

/// <summary>Stanje jednog TS čvora sačuvano tokom Prethodni/Sledeći navigacije.</summary>
public sealed class NodeRoundNodeDraft
{
    public int NodeIndex { get; set; }
    public int NodeNumber { get; set; }
    public bool IsManual { get; set; }
    public ManualCurveMode ManualMode { get; set; } = ManualCurveMode.Lrl;
    public double R { get; set; }
    public double L1 { get; set; }
    public double L2 { get; set; }
    public double R1 { get; set; }
    public double R2 { get; set; }
    public double Rl { get; set; }
    public double Rr { get; set; }
    public double RaRatio { get; set; } = 2.0;
    public bool Prelaznice { get; set; }
}

/// <summary>Stanje dijaloga UredjenjeKrivine (preživljava pick sa crteža).</summary>
public sealed class NodeRoundEditState
{
    public int NodeIndex { get; set; }
    public bool IsManual { get; set; }
    public ManualCurveMode ManualMode { get; set; } = ManualCurveMode.Lrl;
    public double R { get; set; }
    public double L1 { get; set; }
    public double L2 { get; set; }
    public double R1 { get; set; }
    public double R2 { get; set; }
    public double Rl { get; set; }
    public double Rr { get; set; }
    public double RaRatio { get; set; } = 2.0;
    public bool Prelaznice { get; set; }
    /// <summary>Auto prikaz: true=A1/A2, false=L1/L2.</summary>
    public bool ShowAParameters { get; set; } = true;

    /// <summary>Izmene po indeksu čvora — zadržavaju se dok korisnik ide Prethodni/Sledeći.</summary>
    public Dictionary<int, NodeRoundNodeDraft> NodeDrafts { get; } = new();
}

public partial class NodeRoundEditDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IReadOnlyList<TangentNodeInfo> _nodes;
    private readonly double _defaultRadius;
    private readonly NodeRoundEditState _state;
    private int _index;
    private bool _loading;
    private bool _syncing;

    public NodeRoundCloseAction CloseAction { get; private set; } = NodeRoundCloseAction.Cancel;
    public NodeRoundEditState State => _state;
    public int SelectedNodeNumber => _nodes[_index].Number;
    public int SelectedArcElementIndex => _nodes[_index].ArcElementIndex;
    public Point3d CurrentPi => Current.Pi;
    public double AppliedRadius { get; private set; }
    public bool Applied => CloseAction == NodeRoundCloseAction.Applied;

    /// <summary>Sve sačuvane izmene po TS čvorovima (redosled po indeksu).</summary>
    public IReadOnlyList<NodeRoundNodeDraft> PendingEdits =>
        _state.NodeDrafts.Values
            .OrderBy(draft => draft.NodeIndex)
            .ToList();

    public NodeRoundEditDialog(
        IReadOnlyList<TangentNodeInfo> nodes,
        NodeRoundEditState state,
        double defaultRadius,
        Window? owner = null)
    {
        if (nodes is null || nodes.Count == 0)
        {
            throw new ArgumentException("Nema TS cvorova za uredjivanje.", nameof(nodes));
        }

        _nodes = nodes;
        _state = state;
        _defaultRadius = defaultRadius > 1e-6 ? defaultRadius : 50;
        _index = MathNet48.Clamp(state.NodeIndex, 0, nodes.Count - 1);

        _loading = true;
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        LoadCurrent();
    }

    private TangentNodeInfo Current => _nodes[_index];

    private void LoadCurrent()
    {
        _loading = true;
        try
        {
            Title = $"UredjenjeKrivine [TS{Current.Number}]";
            _state.NodeIndex = _index;

            if (_state.NodeDrafts.TryGetValue(_index, out var draft))
            {
                ApplyDraftToState(draft);
            }
            else
            {
                SeedStateFromNode(Current);
            }

            AppliedRadius = _state.R;
            RBox.Text = F(_state.R);
            RlBox.Text = F(_state.Rl);
            RrBox.Text = F(_state.Rr);
            L1Box.Text = F(_state.L1);
            L2Box.Text = F(_state.L2);
            R1Box.Text = F(_state.R1);
            R2Box.Text = F(_state.R2);
            RaBox.Text = F(_state.RaRatio);
            PrelazniceBox.IsChecked = _state.Prelaznice;

            if (_state.IsManual)
            {
                ManualRadio.IsChecked = true;
            }
            else
            {
                AutoRadio.IsChecked = true;
            }

            ManualModeBox.SelectedIndex = (int)_state.ManualMode;
            PrevBtn.IsEnabled = _index > 0;
            NextBtn.IsEnabled = _index < _nodes.Count - 1;
            ApplyEnableState();
            UpdateTransitionParameterDisplay();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SeedStateFromNode(TangentNodeInfo node)
    {
        _state.R = node.Radius > 1e-6 ? node.Radius : _defaultRadius;
        _state.Rl = node.TangentLength1 > 1e-6 ? node.TangentLength1 : 0;
        _state.Rr = node.TangentLength2 > 1e-6 ? node.TangentLength2 : 0;
        _state.L1 = node.L1;
        _state.L2 = node.L2;
        _state.Prelaznice = node.L1 > 1e-6 || node.L2 > 1e-6;
        _state.R1 = 0;
        _state.R2 = 0;
        _state.IsManual = node.L1 > 1e-6 || node.L2 > 1e-6;
        if (_state.Rl <= 1e-6 || _state.Rr <= 1e-6)
        {
            var half = node.DeflectionRadians / 2.0;
            var tanHalf = Math.Tan(half);
            if (tanHalf > 1e-12)
            {
                var tangent = _state.R * tanHalf;
                if (_state.Rl <= 1e-6)
                {
                    _state.Rl = tangent;
                }

                if (_state.Rr <= 1e-6)
                {
                    _state.Rr = tangent;
                }
            }
        }
    }

    private void ApplyDraftToState(NodeRoundNodeDraft draft)
    {
        _state.IsManual = draft.IsManual;
        _state.ManualMode = draft.ManualMode;
        _state.R = draft.R;
        _state.L1 = draft.L1;
        _state.L2 = draft.L2;
        _state.R1 = draft.R1;
        _state.R2 = draft.R2;
        _state.Rl = draft.Rl;
        _state.Rr = draft.Rr;
        _state.RaRatio = draft.RaRatio;
        _state.Prelaznice = draft.Prelaznice;
    }

    private void PersistCurrentDraft()
    {
        SnapshotToState();
        _state.NodeDrafts[_index] = new NodeRoundNodeDraft
        {
            NodeIndex = _index,
            NodeNumber = Current.Number,
            IsManual = _state.IsManual,
            ManualMode = _state.ManualMode,
            R = _state.R,
            L1 = _state.L1,
            L2 = _state.L2,
            R1 = _state.R1,
            R2 = _state.R2,
            Rl = _state.Rl,
            Rr = _state.Rr,
            RaRatio = _state.RaRatio,
            Prelaznice = _state.Prelaznice
        };
    }

    private void SnapshotToState()
    {
        _state.NodeIndex = _index;
        _state.IsManual = ManualRadio.IsChecked == true;
        _state.ManualMode = (ManualCurveMode)MathNet48.Clamp(ManualModeBox.SelectedIndex, 0, 3);
        if (TryParse(RBox.Text, out var r))
        {
            _state.R = r;
        }

        ReadTransitionValuesToState();

        if (TryParse(R1Box.Text, out var r1))
        {
            _state.R1 = r1;
        }

        if (TryParse(R2Box.Text, out var r2))
        {
            _state.R2 = r2;
        }

        if (TryParse(RlBox.Text, out var rl))
        {
            _state.Rl = rl;
        }

        if (TryParse(RrBox.Text, out var rr))
        {
            _state.Rr = rr;
        }

        if (TryParse(RaBox.Text, out var ra) && ra > 1e-9)
        {
            _state.RaRatio = ra;
        }

        _state.Prelaznice = PrelazniceBox.IsChecked == true;

        // Primena samo aktivnih parametara prema režimu (LR → L2=0, RL → L1=0, Auto → bez prelaznica).
        NormalizeActiveLengths();
        AppliedRadius = _state.R;
    }

    private void ReadTransitionValuesToState()
    {
        if (!TryParse(L1Box.Text, out var first) ||
            !TryParse(L2Box.Text, out var second))
        {
            return;
        }

        var autoWithTransitions =
            ManualRadio.IsChecked != true &&
            PrelazniceBox.IsChecked == true;
        if (autoWithTransitions && _state.ShowAParameters && _state.R > 1e-9)
        {
            // Plateia: L = A² / R.
            _state.L1 = first * first / _state.R;
            _state.L2 = second * second / _state.R;
            return;
        }

        _state.L1 = first;
        _state.L2 = second;
    }

    private void UpdateTransitionParameterDisplay()
    {
        var autoWithTransitions =
            ManualRadio.IsChecked != true &&
            PrelazniceBox.IsChecked == true;
        var showA = autoWithTransitions && _state.ShowAParameters;

        L1Label.Text = showA ? "A1" : "L1";
        L2Label.Text = showA ? "A2" : "L2";
        ParamABtn.Content = showA ? "L" : "A";
        ParamABtn.IsEnabled = autoWithTransitions;

        if (showA)
        {
            var radius = _state.R > 1e-9 ? _state.R : 0;
            L1Box.Text = F(Math.Sqrt(Math.Max(0, _state.L1 * radius)));
            L2Box.Text = F(Math.Sqrt(Math.Max(0, _state.L2 * radius)));
        }
        else
        {
            L1Box.Text = F(_state.L1);
            L2Box.Text = F(_state.L2);
        }
    }

    private void RecalcAutoTransitions(double radius)
    {
        if (ManualRadio.IsChecked == true ||
            PrelazniceBox.IsChecked != true ||
            !TryParse(RaBox.Text, out var ratio) ||
            ratio <= 1e-9)
        {
            if (ManualRadio.IsChecked != true && PrelazniceBox.IsChecked != true)
            {
                _state.L1 = 0;
                _state.L2 = 0;
            }

            UpdateTransitionParameterDisplay();
            return;
        }

        // Plateia: A = R / (R/A), zatim L = A² / R.
        var a = radius / ratio;
        var length = a * a / radius;
        _state.RaRatio = ratio;
        _state.L1 = length;
        _state.L2 = length;
        UpdateTransitionParameterDisplay();
    }

    /// <summary>
    /// LR/RL/LRL/Auto: isključi L1/L2 koji nisu deo režima (iako TextBox još prikazuje staru vrednost).
    /// </summary>
    private void NormalizeActiveLengths()
    {
        if (!_state.IsManual)
        {
            if (!_state.Prelaznice)
            {
                _state.L1 = 0;
                _state.L2 = 0;
            }

            return;
        }

        switch (_state.ManualMode)
        {
            case ManualCurveMode.Lr:
                // L1 + R — bez izlazne prelaznice
                if (L1Lock.IsChecked != true)
                {
                    _state.L1 = 0;
                }

                _state.L2 = 0;
                break;
            case ManualCurveMode.Rl:
                // R + L2 — bez ulazne prelaznice
                _state.L1 = 0;
                if (L2Lock.IsChecked != true)
                {
                    _state.L2 = 0;
                }

                break;
            case ManualCurveMode.Ll:
                if (L1Lock.IsChecked != true)
                {
                    _state.L1 = 0;
                }

                if (L2Lock.IsChecked != true)
                {
                    _state.L2 = 0;
                }

                break;
            case ManualCurveMode.Lrl:
            default:
                if (L1Lock.IsChecked != true)
                {
                    _state.L1 = 0;
                }

                if (L2Lock.IsChecked != true)
                {
                    _state.L2 = 0;
                }

                break;
        }
    }

    private void ApplyEnableState()
    {
        var manual = ManualRadio.IsChecked == true;
        ManualModeBox.IsEnabled = manual;

        // Auto: samo R (i opciono prelaznice R/A).
        if (!manual)
        {
            SetRow(R1Lock, R1Box, enabled: false, locked: false);
            SetRow(L1Lock, L1Box, enabled: false, locked: false);
            SetRow(RLock, RBox, enabled: true, locked: true);
            SetRow(L2Lock, L2Box, enabled: false, locked: false);
            SetRow(R2Lock, R2Box, enabled: false, locked: false);
            SetRow(RlLock, RlBox, enabled: false, locked: false);
            SetRow(RrLock, RrBox, enabled: false, locked: false);
            PrelazniceBox.IsEnabled = true;
            RaBox.IsEnabled = PrelazniceBox.IsChecked == true;
            ParamABtn.IsEnabled = PrelazniceBox.IsChecked == true;
            return;
        }

        var mode = (ManualCurveMode)MathNet48.Clamp(ManualModeBox.SelectedIndex, 0, 3);
        // Plateia Ručno: LRL / LR / RL / LL
        var useL1 = mode is ManualCurveMode.Lrl or ManualCurveMode.Lr or ManualCurveMode.Ll;
        var useL2 = mode is ManualCurveMode.Lrl or ManualCurveMode.Rl or ManualCurveMode.Ll;
        var useR = mode is not ManualCurveMode.Ll; // LL → R se računa

        SetRow(R1Lock, R1Box, enabled: false, locked: false);
        SetRow(R2Lock, R2Box, enabled: false, locked: false);
        SetRow(L1Lock, L1Box, enabled: useL1, locked: useL1);
        SetRow(L2Lock, L2Box, enabled: useL2, locked: useL2);
        SetRow(RLock, RBox, enabled: useR, locked: useR);
        SetRow(RlLock, RlBox, enabled: true, locked: RlLock.IsChecked == true);
        SetRow(RrLock, RrBox, enabled: true, locked: RrLock.IsChecked == true);

        // U ručnom režimu sa prelaznicama L1/L2 — Prelaznice info.
        PrelazniceBox.IsEnabled = true;
        PrelazniceBox.IsChecked = useL1 || useL2;
        RaBox.IsEnabled = PrelazniceBox.IsChecked == true;
        ParamABtn.IsEnabled = false;
    }

    private static void SetRow(CheckBox lockBox, TextBox valueBox, bool enabled, bool locked)
    {
        lockBox.IsEnabled = enabled;
        if (enabled)
        {
            lockBox.IsChecked = locked;
            valueBox.IsEnabled = locked;
        }
        else
        {
            lockBox.IsChecked = false;
            valueBox.IsEnabled = false;
        }
    }

    private void OnTipChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _nodes is null || _nodes.Count == 0)
        {
            return;
        }

        ApplyEnableState();
        if (AutoRadio.IsChecked == true && TryParse(RBox.Text, out var r) && r > 1e-6)
        {
            RecalcFromRadius(r);
        }
        else if (ManualRadio.IsChecked == true &&
                 (ManualCurveMode)ManualModeBox.SelectedIndex == ManualCurveMode.Ll)
        {
            RecalcRadiusFromLengths();
        }

        UpdateTransitionParameterDisplay();
    }

    private void OnManualModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ApplyEnableState();
        if ((ManualCurveMode)ManualModeBox.SelectedIndex == ManualCurveMode.Ll)
        {
            RecalcRadiusFromLengths();
        }

        UpdateTransitionParameterDisplay();
    }

    private void OnRaChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _syncing ||
            ManualRadio.IsChecked == true ||
            PrelazniceBox.IsChecked != true ||
            !TryParse(RBox.Text, out var radius) ||
            radius <= 1e-6)
        {
            return;
        }

        RecalcAutoTransitions(radius);
    }

    private void OnParamChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _syncing || _nodes is null || _nodes.Count == 0)
        {
            return;
        }

        if (ManualRadio.IsChecked != true && !ReferenceEquals(sender, RBox))
        {
            return;
        }

        _syncing = true;
        try
        {
            if (ReferenceEquals(sender, RBox) && TryParse(RBox.Text, out var r) && r > 1e-6)
            {
                RecalcFromRadius(r);
            }
            else if ((ReferenceEquals(sender, RlBox) || ReferenceEquals(sender, RrBox)) &&
                     (RlLock.IsChecked == true || RrLock.IsChecked == true))
            {
                var text = ReferenceEquals(sender, RlBox) ? RlBox.Text : RrBox.Text;
                if (TryParse(text, out var t) && t > 1e-6)
                {
                    RecalcFromTangent(t);
                }
            }
            else if ((ReferenceEquals(sender, L1Box) || ReferenceEquals(sender, L2Box)) &&
                     (ManualCurveMode)ManualModeBox.SelectedIndex == ManualCurveMode.Ll)
            {
                RecalcRadiusFromLengths();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RecalcFromRadius(double radius)
    {
        var half = Current.DeflectionRadians / 2.0;
        var tanHalf = Math.Tan(half);
        if (tanHalf < 1e-12)
        {
            return;
        }

        var t = radius * tanHalf;
        AppliedRadius = radius;
        _state.R = radius;
        if (RlLock.IsChecked != true)
        {
            RlBox.Text = F(t);
            _state.Rl = t;
        }

        if (RrLock.IsChecked != true)
        {
            RrBox.Text = F(t);
            _state.Rr = t;
        }

        RecalcAutoTransitions(radius);
    }

    private void RecalcFromTangent(double tangentLength)
    {
        var half = Current.DeflectionRadians / 2.0;
        var tanHalf = Math.Tan(half);
        if (tanHalf < 1e-12)
        {
            return;
        }

        var r = tangentLength / tanHalf;
        AppliedRadius = r;
        _state.R = r;
        RBox.Text = F(r);
        if (ReferenceEquals(RlLock.IsChecked, true) || RlLock.IsChecked == true)
        {
            // keep typed side; sync other if unlocked
        }

        if (RlLock.IsChecked != true)
        {
            RlBox.Text = F(tangentLength);
            _state.Rl = tangentLength;
        }

        if (RrLock.IsChecked != true)
        {
            RrBox.Text = F(tangentLength);
            _state.Rr = tangentLength;
        }
    }

    /// <summary>
    /// LL režim: aproksimacija R iz L1/L2 i R/A (Plateia stil, bez pune klotide u crtežu).
    /// A = R / (R/A); L ≈ A²/R → R ≈ L · (R/A)² za svaku stranu; prosek.
    /// </summary>
    private void RecalcRadiusFromLengths()
    {
        if (!TryParse(RaBox.Text, out var ra) || ra < 1e-9)
        {
            ra = 2.0;
        }

        TryParse(L1Box.Text, out var l1);
        TryParse(L2Box.Text, out var l2);
        var lengths = new List<double>();
        if (l1 > 1e-6)
        {
            lengths.Add(l1);
        }

        if (l2 > 1e-6)
        {
            lengths.Add(l2);
        }

        if (lengths.Count == 0)
        {
            return;
        }

        var r = lengths.Average(l => l * ra * ra);
        if (r <= 1e-6)
        {
            return;
        }

        AppliedRadius = r;
        _state.R = r;
        RBox.Text = F(r);
        RecalcFromRadius(r);
    }

    private void OnPickR(object sender, RoutedEventArgs e) => CloseForPick(NodeRoundCloseAction.PickRadius);

    private void OnPickRl(object sender, RoutedEventArgs e) => CloseForPick(NodeRoundCloseAction.PickTangentLeft);

    private void OnPickRr(object sender, RoutedEventArgs e) => CloseForPick(NodeRoundCloseAction.PickTangentRight);

    private void CloseForPick(NodeRoundCloseAction action)
    {
        PersistCurrentDraft();
        CloseAction = action;
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (TryParse(RBox.Text, out var r) && r > 1e-6)
        {
            RecalcFromRadius(r);
        }
        else if ((ManualCurveMode)ManualModeBox.SelectedIndex == ManualCurveMode.Ll)
        {
            RecalcRadiusFromLengths();
        }
    }

    /// <summary>Plateia prikaz: menja A1/A2 ↔ L1/L2 bez promene geometrije.</summary>
    private void OnParamAClick(object sender, RoutedEventArgs e)
    {
        if (ManualRadio.IsChecked == true || PrelazniceBox.IsChecked != true)
        {
            return;
        }

        ReadTransitionValuesToState();
        _state.ShowAParameters = !_state.ShowAParameters;
        UpdateTransitionParameterDisplay();
    }

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        if (_index <= 0)
        {
            return;
        }

        PersistCurrentDraft();
        _index--;
        LoadCurrent();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index >= _nodes.Count - 1)
        {
            return;
        }

        PersistCurrentDraft();
        _index++;
        LoadCurrent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SnapshotToState();
        if ((ManualCurveMode)ManualModeBox.SelectedIndex == ManualCurveMode.Ll)
        {
            RecalcRadiusFromLengths();
            SnapshotToState();
        }

        if (_state.R <= 1e-6)
        {
            MessageBox.Show(this, "Unesite ispravan radijus R (> 0).", "TCM-ROADS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PersistCurrentDraft();
        AppliedRadius = _state.R;
        CloseAction = NodeRoundCloseAction.Applied;
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseAction = NodeRoundCloseAction.Cancel;
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private static string F(double v) => v.ToString("0.######", Inv);

    private static bool TryParse(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, Inv, out value) ||
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
