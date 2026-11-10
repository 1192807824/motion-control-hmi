using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Controls;

internal static partial class Program
{
    private static void CheckIncrementalStatistics()
    {
        var rows = Enumerable.Range(0, 30_000).Select(i => new BatchMeasurement
        {
            StationNumber = i % 2 + 5, Instrument = i % 2 == 0 ? "E4981A" : "SM7110",
            MeasurementMode = i % 2 == 0 ? "C" : "R", Value = i % 17 == 0 ? null : 0.1 + i % 101 * 0.001,
            ValidReading = i % 23 != 0, StationPassed = i % 7 != 0, InstrumentStatus = i % 31 == 0 ? 1 : 0,
            RawResponse = "raw reading", Description = "full record stays in SQLite"
        }).ToArray();
        var statistics = new BatchMeasurementStatistics();
        foreach (var row in rows) statistics.Add(row);
        Require(statistics.Count == rows.Length, "Incremental statistics lost rows");
        foreach (var group in rows.GroupBy(r => (r.StationNumber, r.Instrument, r.MeasurementMode, r.Unit)))
        {
            var summary = statistics.Find(group.Key.StationNumber, group.Key.Instrument, group.Key.MeasurementMode, group.Key.Unit)!;
            var expected = MeasurementDistribution.Calculate(group.Where(r => r.HasDistributionSample).Select(r => r.Value!.Value));
            var actual = summary.Distribution;
            Require(summary.Count == group.Count() && summary.Passed == group.Count(r => r.CountsAsOk) &&
                summary.Ng == group.Count(r => r.CountsAsNg) && summary.Unmeasured == group.Count(r => r.IsUnmeasured),
                "Incremental classification differs from full-batch classification");
            Require(actual.Count == expected.Count && actual.Mean == expected.Mean &&
                actual.StandardDeviation == expected.StandardDeviation && actual.Minimum == expected.Minimum &&
                actual.Maximum == expected.Maximum && actual.Values.Length == 0,
                "Incremental statistics must match the full batch without retaining samples");
            CheckHistogram(actual, expected.Values);
        }
        statistics.Clear();
        Require(statistics.Count == 0 && statistics.Find(5, "E4981A", "C", "") is null, "Batch reset retains old samples");
        statistics.Add(new BatchMeasurement { StationNumber = 6, Instrument = "SM7110", MeasurementMode = "R", Unit = "Ω", Value = 1e9, ValidReading = true });
        statistics.Add(new BatchMeasurement { StationNumber = 6, Instrument = "SM7110", MeasurementMode = "R", Unit = "GΩ", Value = 1, ValidReading = true });
        Require(statistics.Groups.Count == 2 && statistics.Find(6, "SM7110", "R", "GΩ")!.Distribution.Mean == 1,
            "Historical records with different units must not be mixed into one distribution");

        // Allocation regression: all existing samples must not be copied on each append/refresh.
        var sample = rows[2];
        var accumulator = new MeasurementSummary();
        var chart = new MeasurementDistributionChart();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 30_000; i++)
        {
            accumulator.Add(sample);
            chart.SetSummary("test", "nF", accumulator);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(accumulator.Count == 30_000 && accumulator.Distribution.Count == 30_000 && allocated < 16_000_000,
            $"Incremental chart updates must allocate linearly, allocated={allocated}");
        var updateStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++) accumulator.Add(sample);
        Require(GC.GetAllocatedBytesForCurrentThread() - updateStart < 1024 && accumulator.Distribution.Values.Length == 0,
            "One million additional measurements must not allocate or retain sample storage");
        foreach (var values in new[]
        {
            Enumerable.Range(-5000, 10001).Select(i => i * 0.001).ToArray(),
            Enumerable.Range(-5000, 10001).Select(i => i * 0.001).Reverse().ToArray(),
            new[] { 0d, 1, -1, 1000, -1000, 0.5, -0.5, 100_000, -100_000 },
            new[] { 7d, 7, 7, 7 }, new[] { -7d, -7, -7, -7 }
        })
        {
            var summary = new MeasurementSummary();
            foreach (var value in values) summary.Add(new BatchMeasurement { Value = value, ValidReading = true });
            CheckHistogram(summary.Distribution, values);
        }
        Console.WriteLine($"PASS: 30,000-row incremental statistics with zero retained samples; exact merged histogram counts; update allocations={allocated:N0} bytes.");
    }

    private static void CheckHistogram(MeasurementDistribution actual, double[] samples)
    {
        var expected = new int[16];
        var width = (actual.HistogramHigh - actual.HistogramLow) / 16;
        foreach (var value in samples) expected[Math.Clamp((int)((value - actual.HistogramLow) / width), 0, 15)]++;
        Require(actual.HistogramCounts!.SequenceEqual(expected) && expected.Sum() == actual.Count,
            "Merged histogram must equal a full scan at the displayed bucket boundaries");
    }
}
