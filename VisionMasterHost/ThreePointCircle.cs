using System;
using System.Windows;

namespace VisionMasterHost;

internal static class ThreePointCircle
{
    public static bool TryFit(Point first, Point second, Point third, out Point center, out double radius)
    {
        center = default;
        radius = 0;
        var a = second - first;
        var b = third - first;
        var cross = a.X * b.Y - a.Y * b.X;
        var scale = Math.Max(a.LengthSquared, Math.Max(b.LengthSquared, (third - second).LengthSquared));
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ||
            Math.Abs(cross) <= scale * 0.000001)
        {
            return false;
        }

        var x = (b.Y * a.LengthSquared - a.Y * b.LengthSquared) / (2 * cross);
        var y = (a.X * b.LengthSquared - b.X * a.LengthSquared) / (2 * cross);
        center = new Point(first.X + x, first.Y + y);
        radius = Math.Sqrt(x * x + y * y);
        return !double.IsNaN(radius) && !double.IsInfinity(radius) && radius > 0 &&
               !double.IsNaN(center.X) && !double.IsInfinity(center.X) &&
               !double.IsNaN(center.Y) && !double.IsInfinity(center.Y);
    }
}
