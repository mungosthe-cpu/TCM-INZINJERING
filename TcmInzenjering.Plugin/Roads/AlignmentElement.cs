using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads;

internal sealed class AlignmentElement
{
    public AlignmentElementType Type { get; init; }
    public Point3d Start { get; init; }
    public Point3d End { get; init; }
    public double Length { get; init; }
    public double StartStation { get; set; }
    public double EndStation { get; set; }
    public double Radius { get; init; }
    public Point3d Center { get; init; }
    public bool Clockwise { get; init; }

    /// <summary>Klotoida: uzorci duž luka (XY). Null za Tangent/Arc.</summary>
    public IReadOnlyList<Point3d>? SpiralPoints { get; init; }

    /// <summary>Dužina prelaznice L (isto kao Length za Spiral).</summary>
    public double SpiralLength => Type == AlignmentElementType.Spiral ? Length : 0;

    /// <summary>A = √(R·L) za prelaznicu.</summary>
    public double SpiralA { get; init; }
}
