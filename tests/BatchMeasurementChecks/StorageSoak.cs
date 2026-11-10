using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void RunStorageSoak(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-storage-soak-" + Guid.NewGuid().ToString("N"));
        var store = new BatchMeasurementStore(root);
        var statistics = new BatchMeasurementStatistics();
        var watch = Stopwatch.StartNew();
        var product = Guid.NewGuid();
        var measuredAt = DateTimeOffset.Now;
        for (var i = 0; i < count; i++)
        {
            if (i % 2 == 0) product = Guid.NewGuid();
            var row = new BatchMeasurement { BatchNumber = "LONG-RUN", ProductId = product,
                StationNumber = 5 + i % 2, Instrument = i % 2 == 0 ? "E4981A" : "SM7110",
                MeasurementMode = i % 2 == 0 ? "C" : "R", Unit = i % 2 == 0 ? "nF" : "GΩ",
                Value = 1 + (i % 101) * 0.001, ValidReading = true, StationPassed = i % 10 != 0,
                RawResponse = "0,1.01,0.001,1", Description = $"record {i}", MeasuredAt = measuredAt.AddMilliseconds(i) };
            store.Append(row);
            statistics.Add(row);
            if ((i + 1) % 5000 == 0)
            {
                using var process = Process.GetCurrentProcess();
                Console.WriteLine($"STORAGE n={i + 1} seconds={watch.Elapsed.TotalSeconds:F1} privateMB={process.PrivateMemorySize64 / 1048576d:F1} managedMB={GC.GetTotalMemory(false) / 1048576d:F1}");
            }
        }
        var restored = new BatchMeasurementStore(root).ReadBatchStatistics("LONG-RUN");
        Require(restored.Count == count, "Restart lost stored records");
        foreach (var group in statistics.Groups)
        {
            var loaded = restored.Groups[group.Key];
            Require(loaded.Distribution.Values.Length == 0 && loaded.Distribution.Count == group.Value.Distribution.Count &&
                loaded.Distribution.Mean == group.Value.Distribution.Mean &&
                loaded.Distribution.StandardDeviation == group.Value.Distribution.StandardDeviation &&
                loaded.Distribution.HistogramCounts!.SequenceEqual(group.Value.Distribution.HistogramCounts!),
                "Restart streaming summary differs from incremental live summary");
        }
        var first = store.ReadBatchPage("LONG-RUN", 0);
        var next = store.ReadBatchPage("LONG-RUN", 1);
        var last = store.ReadBatchPage("LONG-RUN", int.MaxValue);
        Require(first.Rows.Count == Math.Min(count, 500) && first.Statistics.Count == count && first.Products == (count + 1) / 2,
            "Paged detail must retain global counts and distinct products");
        if (count > 500) Require(next.Rows[0].Description == "record 500", "Second page skipped or duplicated boundary");
        Require(last.Rows.Count <= 500 && last.Rows[^1].Description == $"record {count - 1}", "Last page must remain reachable");
        var csv = Path.Combine(root, "full.csv");
        Require(BatchMeasurementCsvExporter.Export(csv, store.EnumerateBatch("LONG-RUN")) == count &&
            File.ReadLines(csv).Count() == count + 1, "Streamed export must include every record, not only a page");

        _ = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var page = new BatchQueryPage(store);
        Wait(page.ActivateAsync("LONG-RUN"));
        var visible = (IReadOnlyList<BatchMeasurement>)typeof(BatchQueryPage).GetField("_records", Flags)!.GetValue(page)!;
        var total = (TextBlock)typeof(BatchQueryPage).GetField("_metricRecords", Flags)!.GetValue(page)!;
        Require(visible.Count <= 500 && total.Text == count.ToString("N0"), "Query UI retained full batch or showed page-only totals");
        Wait((Task)typeof(BatchQueryPage).GetMethod("QueryPageAsync", Flags)!.Invoke(page, [1])!);
        visible = (IReadOnlyList<BatchMeasurement>)typeof(BatchQueryPage).GetField("_records", Flags)!.GetValue(page)!;
        if (count > 500) Require(visible[0].Description == "record 500", "UI next page did not load expected records");
        using var end = Process.GetCurrentProcess();
        Console.WriteLine($"PASS: {count:N0} durable individual commits, restart statistics, 500-row query pages, full streamed export; privateMB={end.PrivateMemorySize64 / 1048576d:F1}; seconds={watch.Elapsed.TotalSeconds:F1}; fixture={root}");
    }
}
