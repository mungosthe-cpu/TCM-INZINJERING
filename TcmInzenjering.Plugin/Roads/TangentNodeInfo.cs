using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

/// <summary>
/// Parametri čvora tangentnog poligona (preseka dva pravca / PI).
/// </summary>
public sealed class TangentNodeInfo
{
    /// <summary>Redni broj čvora T1, T2, … (1-based).</summary>
    public int Number { get; init; }

    /// <summary>Indeks luk elementa u osi.</summary>
    public int ArcElementIndex { get; init; }

    public Point3d Pi { get; init; }
    public double DeflectionRadians { get; init; }
    public double Radius { get; init; }
    public double ArcLength { get; init; }
    public double TangentLength1 { get; init; }
    public double TangentLength2 { get; init; }
    public double ExternalDistance { get; init; }
    public double L1 { get; init; }
    public double L2 { get; init; }

    /// <summary>
    /// Jedinični vektor: od PI ka spoljnoj strani ugla tangenti (nasuprot luku / centru).
    /// Tabela i leader idu u ovom smeru.
    /// </summary>
    public Vector3d OpenBisector { get; init; }
}
