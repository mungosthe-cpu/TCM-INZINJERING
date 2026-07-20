namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>Rezultat jedne metode proračuna zapremine (m³, m²).</summary>
public sealed class TerrainVolumeMethodResult
{
    public string MethodName { get; init; } = string.Empty;
    public double CutVolume { get; init; }
    public double FillVolume { get; init; }
    public double CutArea { get; init; }
    public double FillArea { get; init; }
    public double NetVolume => FillVolume - CutVolume;
}

/// <summary>Ćelija mape neslaganja Grid vs TIN.</summary>
public readonly record struct TerrainVolumeDisagreementCell(
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    double TinVolume,
    double GridVolume,
    double AbsoluteDiff);

/// <summary>
/// Kompletan rezultat Panela poverenja: TIN–TIN (glavni), Grid, sekcije,
/// faktori nabujavanja/sleganja i mapa neslaganja.
/// </summary>
public sealed class TerrainVolumeResult
{
    public string BaseName { get; init; } = string.Empty;
    public string ComparisonName { get; init; } = string.Empty;
    public double GridStep { get; init; }
    public int SectionCount { get; init; }
    public double SwellFactor { get; init; } = 1.0;
    public double ShrinkFactor { get; init; } = 1.0;
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }

    public TerrainVolumeMethodResult Tin { get; init; } = new() { MethodName = "TIN–TIN" };
    public TerrainVolumeMethodResult Grid { get; init; } = new() { MethodName = "Grid" };
    public TerrainVolumeMethodResult Sections { get; init; } = new() { MethodName = "Sekcije" };

    public IReadOnlyList<TerrainVolumeDisagreementCell> DisagreementCells { get; init; } =
        Array.Empty<TerrainVolumeDisagreementCell>();

    public double MeanRelativeErrorPercent { get; init; }
    public double MaxRelativeErrorPercent { get; init; }
    public string ConfidenceLevel { get; init; } = "Nepoznato";
    public string ConfidenceNote { get; init; } = string.Empty;
    public string? Warning { get; init; }

    public double AdjustedCut => Tin.CutVolume * SwellFactor;
    public double AdjustedFill => Tin.FillVolume * ShrinkFactor;
    public double AdjustedNet => AdjustedFill - AdjustedCut;

    public DateTime ComputedAt { get; init; } = DateTime.Now;
}
