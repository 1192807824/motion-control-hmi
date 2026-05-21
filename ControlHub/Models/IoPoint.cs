using System.Windows.Media;

namespace ControlHub.Models;

public sealed class IoPoint
{
    public string Name { get; init; } = "";
    public bool IsOn { get; init; }
    public Brush LampBrush => IsOn ? Brushes.LimeGreen : Brushes.DimGray;
}
