using System.IO;
using System.Text.Json;

namespace TcmInzenjering.Plugin.Roads.Terrain;

internal enum ContourSmoothType
{
    AddVertices = 0,
    SplineCurve = 1
}

internal enum SurfaceElevationDisplayMode
{
    UseSurfaceElevation = 0,
    FlattenToElevation = 1,
    ExaggerateElevation = 2
}

internal enum ContourRangeGroupBy
{
    ContourInterval = 0,
    RangeInterval = 1,
    Quantile = 2
}

/// <summary>Civil Surface Style → Display komponenta.</summary>
internal sealed class SurfaceComponentStyle
{
    public string ComponentType { get; set; } = "";
    public bool Visible { get; set; }
    public string Layer { get; set; } = "0";
    public short ColorAci { get; set; } = 7;
    public bool ColorByLayer { get; set; }
    public bool ColorByBlock { get; set; }
    public string Linetype { get; set; } = "Continuous";
    public double LtScale { get; set; } = 1.0;
    public double LineWeightMm { get; set; }
    public bool LineWeightByBlock { get; set; } = true;
}

/// <summary>
/// Civil 3D Surface Style (Information / Borders / Contours / Grid / Points /
/// Triangles / Watersheds / Analysis / Display / Summary).
/// </summary>
internal static class ContourPreferences
{
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string PreferencesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TcmInzenjering",
            "contour-settings.json");

    public static SurfaceStyleSnapshot Current { get; private set; } = SurfaceStyleSnapshot.CreateDefault();

    // Convenience accessors used by draw command.
    public static double MinorInterval => Current.MinorInterval;
    public static double MajorInterval => Current.MajorInterval;
    public static double BaseElevation => Current.BaseElevation;
    public static bool SmoothContours => Current.SmoothContours;
    public static ContourSmoothType SmoothType => Current.SmoothType;
    public static int SmoothFactor => Current.SmoothFactor;
    public static short MinorColorAci => Current.GetComponent("Minor Contour").ColorAci;
    public static short MajorColorAci => Current.GetComponent("Major Contour").ColorAci;
    public static double MinorLineWeightMm => Current.GetComponent("Minor Contour").LineWeightMm;
    public static double MajorLineWeightMm => Current.GetComponent("Major Contour").LineWeightMm;
    public static bool DrawMinor => Current.GetComponent("Minor Contour").Visible;
    public static bool DrawMajor => Current.GetComponent("Major Contour").Visible;
    public static string MinorLayer => Current.GetComponent("Minor Contour").Layer;
    public static string MajorLayer => Current.GetComponent("Major Contour").Layer;
    public static string MinorLinetype => Current.GetComponent("Minor Contour").Linetype;
    public static string MajorLinetype => Current.GetComponent("Major Contour").Linetype;
    public static IReadOnlyList<double> UserContourElevations => Current.ParseUserContours();
    public static string ContourLabelFont => Current.ContourLabelFont;
    public static double ContourLabelHeight => Math.Max(0.1, Current.ContourLabelHeight);
    public static int ContourLabelDecimals => MathNet48.Clamp(Current.ContourLabelDecimals, 0, 6);
    public static short ContourLabelColorAci => Current.ContourLabelColorAci;
    public static bool ContourLabelBackgroundMask => Current.ContourLabelBackgroundMask;

    public static void Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                Current = SurfaceStyleSnapshot.CreateDefault();
                return;
            }

            var dto = JsonSerializer.Deserialize<SurfaceStyleSnapshot>(
                File.ReadAllText(PreferencesPath), JsonOptions);
            Current = dto?.Normalized() ?? SurfaceStyleSnapshot.CreateDefault();
        }
        catch
        {
            Current = SurfaceStyleSnapshot.CreateDefault();
        }
    }

    public static void Save(SurfaceStyleSnapshot snapshot)
    {
        Current = snapshot.Normalized();
        try
        {
            var dir = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Legacy Save signature — maps into snapshot Display components.</summary>
    public static void Save(
        double minorInterval,
        double majorInterval,
        double baseElevation,
        bool smoothContours,
        ContourSmoothType smoothType,
        int smoothFactor,
        short minorColorAci,
        short majorColorAci,
        double minorLineWeightMm,
        double majorLineWeightMm,
        bool drawMinor,
        bool drawMajor)
    {
        var s = Current.Clone();
        s.MinorInterval = minorInterval;
        s.MajorInterval = majorInterval;
        s.BaseElevation = baseElevation;
        s.SmoothContours = smoothContours;
        s.SmoothType = smoothType;
        s.SmoothFactor = smoothFactor;
        var minor = s.GetComponent("Minor Contour");
        minor.Visible = drawMinor;
        minor.ColorAci = minorColorAci;
        minor.ColorByLayer = false;
        minor.LineWeightMm = minorLineWeightMm;
        minor.LineWeightByBlock = minorLineWeightMm <= 0;
        var major = s.GetComponent("Major Contour");
        major.Visible = drawMajor;
        major.ColorAci = majorColorAci;
        major.ColorByLayer = false;
        major.LineWeightMm = majorLineWeightMm;
        major.LineWeightByBlock = majorLineWeightMm <= 0;
        Save(s);
    }
}

internal sealed class SurfaceStyleSnapshot
{
    // Information
    public string StyleName { get; set; } = "Standard";
    public string Description { get; set; } = "TCM stil terena (Civil Surface Style).";
    public string CreatedBy { get; set; } = Environment.UserName;
    public string LastModifiedBy { get; set; } = Environment.UserName;
    public string DateCreated { get; set; } = DateTime.Now.ToString("O");
    public string DateModified { get; set; } = DateTime.Now.ToString("O");

    // Borders
    public SurfaceElevationDisplayMode BorderDisplayMode { get; set; } =
        SurfaceElevationDisplayMode.UseSurfaceElevation;
    public double FlattenBordersElevation { get; set; }
    public double ExaggerateBordersScale { get; set; } = 1.0;
    public bool DisplayExteriorBorders { get; set; } = true;
    public bool DisplayInteriorBorders { get; set; } = true;
    public bool UseDatum { get; set; }
    public bool ProjectGridToDatum { get; set; }
    public double DatumElevation { get; set; }

    // Contours
    public ContourRangeGroupBy ContourRangeGroupBy { get; set; } = ContourRangeGroupBy.ContourInterval;
    public int ContourRangeCount { get; set; } = 8;
    public SurfaceElevationDisplayMode ContourDisplayMode { get; set; } =
        SurfaceElevationDisplayMode.UseSurfaceElevation;
    public double FlattenContoursElevation { get; set; }
    public double ExaggerateContoursScale { get; set; } = 1.0;
    public bool ShowContourLegend { get; set; }
    public double BaseElevation { get; set; }
    public double MinorInterval { get; set; } = 1.0;
    public double MajorInterval { get; set; } = 5.0;
    public bool DisplayDepressionTicks { get; set; }
    public double DepressionTickLength { get; set; } = 1.0;
    public bool SmoothContours { get; set; }
    public ContourSmoothType SmoothType { get; set; } = ContourSmoothType.AddVertices;
    public int SmoothFactor { get; set; } = 50;
    public string MajorDisplayLinetype { get; set; } = "Continuous";
    public string MinorDisplayLinetype { get; set; } = "Continuous";
    public string UserContoursText { get; set; } = "";

    // Kotne oznake (izohipse)
    public string ContourLabelFont { get; set; } = "arial.ttf";
    public double ContourLabelHeight { get; set; } = 1.0;
    public int ContourLabelDecimals { get; set; } = 2;
    public short ContourLabelColorAci { get; set; } = 1;
    public bool ContourLabelBackgroundMask { get; set; } = true;

    // Grid
    public SurfaceElevationDisplayMode GridDisplayMode { get; set; } =
        SurfaceElevationDisplayMode.UseSurfaceElevation;
    public double FlattenGridElevation { get; set; }
    public double ExaggerateGridScale { get; set; } = 1.0;
    public bool UsePrimaryGrid { get; set; }
    public double PrimaryGridInterval { get; set; } = 25.0;
    public double PrimaryGridOrientationDeg { get; set; }
    public bool UseSecondaryGrid { get; set; }
    public double SecondaryGridInterval { get; set; } = 25.0;
    public double SecondaryGridOrientationDeg { get; set; } = 90.0;

    // Points
    public SurfaceElevationDisplayMode PointDisplayMode { get; set; } =
        SurfaceElevationDisplayMode.UseSurfaceElevation;
    public double FlattenPointsElevation { get; set; }
    public double ExaggeratePointsScale { get; set; } = 1.0;
    public string PointScalingMethod { get; set; } = "Size in absolute units";
    public double PointUnits { get; set; } = 3.0;
    public int DataPointSymbol { get; set; } = 2;
    public short DataPointColorAci { get; set; } = 1;
    public bool DataPointColorByLayer { get; set; } = true;
    public int DerivedPointSymbol { get; set; } = 34;
    public short DerivedPointColorAci { get; set; } = 7;
    public bool DerivedPointColorByLayer { get; set; } = true;

    // Triangles
    public SurfaceElevationDisplayMode TriangleDisplayMode { get; set; } =
        SurfaceElevationDisplayMode.UseSurfaceElevation;
    public double FlattenTrianglesElevation { get; set; }
    public double ExaggerateTrianglesScale { get; set; } = 1.0;

    // Watersheds / Analysis (Civil-lite — primenjuje se u ApplyCurrentContourStyleToDrawing)
    public bool ShowWatersheds { get; set; }
    public bool AnalyzeDirections { get; set; }
    public bool AnalyzeElevations { get; set; }
    public bool AnalyzeSlopes { get; set; }
    public bool AnalyzeSlopeArrows { get; set; }

    // Display
    public string ViewDirection { get; set; } = "Plan";
    public List<SurfaceComponentStyle> Components { get; set; } = [];

    public static SurfaceStyleSnapshot CreateDefault()
    {
        var s = new SurfaceStyleSnapshot();
        s.Components = CreateDefaultComponents();
        return s;
    }

    public SurfaceStyleSnapshot Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<SurfaceStyleSnapshot>(json) ?? CreateDefault();
    }

    public SurfaceComponentStyle GetComponent(string type)
    {
        EnsureComponents();
        var hit = Components.FirstOrDefault(c =>
            string.Equals(c.ComponentType, type, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            return hit;
        }

        hit = new SurfaceComponentStyle { ComponentType = type, Visible = false };
        Components.Add(hit);
        return hit;
    }

    public IReadOnlyList<double> ParseUserContours()
    {
        if (string.IsNullOrWhiteSpace(UserContoursText))
        {
            return Array.Empty<double>();
        }

        var list = new List<double>();
        foreach (var part in UserContoursText.Split(
                     new[] { ',', ';', ' ', '\t', '\n', '\r' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(part.Trim().Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var z))
            {
                list.Add(z);
            }
        }

        return list;
    }

    public SurfaceStyleSnapshot Normalized()
    {
        EnsureComponents();
        MinorInterval = Math.Max(1e-6, MinorInterval);
        MajorInterval = Math.Max(MinorInterval, MajorInterval);
        var ratio = MajorInterval / MinorInterval;
        if (Math.Abs(ratio - Math.Round(ratio)) > 1e-9)
        {
            MajorInterval = Math.Ceiling(ratio) * MinorInterval;
        }

        SmoothFactor = MathNet48.Clamp(SmoothFactor, 0, 100);
        ContourRangeCount = MathNet48.Clamp(ContourRangeCount, 1, 256);
        PrimaryGridInterval = Math.Max(1e-6, PrimaryGridInterval);
        SecondaryGridInterval = Math.Max(1e-6, SecondaryGridInterval);
        PointUnits = Math.Max(1e-6, PointUnits);
        DepressionTickLength = Math.Max(0, DepressionTickLength);
        ContourLabelHeight = Math.Max(0.1, ContourLabelHeight);
        ContourLabelDecimals = MathNet48.Clamp(ContourLabelDecimals, 0, 6);
        ContourLabelFont = string.IsNullOrWhiteSpace(ContourLabelFont)
            ? "arial.ttf"
            : StationFontCatalog.ResolveFileName(ContourLabelFont);

        ExaggerateBordersScale = Math.Max(0, ExaggerateBordersScale);
        ExaggerateContoursScale = Math.Max(0, ExaggerateContoursScale);
        ExaggerateGridScale = Math.Max(0, ExaggerateGridScale);
        ExaggeratePointsScale = Math.Max(0, ExaggeratePointsScale);
        ExaggerateTrianglesScale = Math.Max(0, ExaggerateTrianglesScale);

        // Sync Contours tab linetype fields ↔ Display components.
        var major = GetComponent("Major Contour");
        var minor = GetComponent("Minor Contour");
        if (!string.IsNullOrWhiteSpace(MajorDisplayLinetype))
        {
            major.Linetype = MajorDisplayLinetype;
        }
        else
        {
            MajorDisplayLinetype = major.Linetype;
        }

        if (!string.IsNullOrWhiteSpace(MinorDisplayLinetype))
        {
            minor.Linetype = MinorDisplayLinetype;
        }
        else
        {
            MinorDisplayLinetype = minor.Linetype;
        }

        if (string.IsNullOrWhiteSpace(major.Layer) || major.Layer == "0")
        {
            major.Layer = "TCM_IZO_MAJOR";
        }

        if (string.IsNullOrWhiteSpace(minor.Layer) || minor.Layer == "0")
        {
            minor.Layer = "TCM_IZO_MINOR";
        }

        LastModifiedBy = Environment.UserName;
        DateModified = DateTime.Now.ToString("O");
        return this;
    }

    private void EnsureComponents()
    {
        if (Components is null || Components.Count == 0)
        {
            Components = CreateDefaultComponents();
            return;
        }

        foreach (var def in CreateDefaultComponents())
        {
            if (!Components.Any(c =>
                    string.Equals(c.ComponentType, def.ComponentType, StringComparison.OrdinalIgnoreCase)))
            {
                Components.Add(def);
            }
        }
    }

    private static List<SurfaceComponentStyle> CreateDefaultComponents() =>
    [
        Comp("Points", false, "TCM_TER_POINTS", 1, false),
        Comp("Triangles", true, "TCM_TEREN", 4, false),
        Comp("Border", true, "TCM_TER_BOUND", 2, false),
        Comp("Major Contour", true, "TCM_IZO_MAJOR", 3, false, lineWeightMm: 0.35, lwByBlock: false),
        Comp("Minor Contour", true, "TCM_IZO_MINOR", 42, false, lineWeightMm: 0, lwByBlock: true),
        Comp("User Contours", false, "TCM_IZO_USER", 7, true),
        Comp("Gridded", false, "TCM_TER_GRID", 6, false),
        Comp("Directions", false, "TCM_SLOPE_ARROW", 7, false),
        Comp("Elevations", false, "TCM_TEREN", 7, true),
        Comp("Slopes", false, "TCM_TEREN", 7, true),
        Comp("Slope Arrows", false, "TCM_SLOPE_ARROW", 7, false),
        Comp("Watersheds", false, "TCM_WATERSHED", 1, false)
    ];

    private static SurfaceComponentStyle Comp(
        string type,
        bool visible,
        string layer,
        short aci,
        bool byLayer,
        double lineWeightMm = 0,
        bool lwByBlock = true) =>
        new()
        {
            ComponentType = type,
            Visible = visible,
            Layer = layer,
            ColorAci = aci,
            ColorByLayer = byLayer,
            Linetype = "Continuous",
            LtScale = 1.0,
            LineWeightMm = lineWeightMm,
            LineWeightByBlock = lwByBlock
        };
}
