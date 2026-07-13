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
}
