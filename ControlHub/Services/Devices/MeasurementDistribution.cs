namespace ControlHub.Services.Devices;

public sealed record MeasurementDistribution(int Count, double Mean, double StandardDeviation,
    double Minimum, double Maximum, double[] Values)
{
    public static MeasurementDistribution Calculate(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).ToArray();
        double mean = 0, sumSquares = 0;
        var count = 0;
        foreach (var value in values)
        {
            var delta = value - mean;
            mean += delta / ++count;
            sumSquares += delta * (value - mean);
        }
        return new(count, mean, count > 1 ? Math.Sqrt(Math.Max(0, sumSquares / (count - 1))) : 0,
            count > 0 ? values.Min() : 0, count > 0 ? values.Max() : 0, values);
    }
}
