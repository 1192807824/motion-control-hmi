using System.IO;
using System.Windows.Controls;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Controls;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckUnmeasuredStatistics()
    {
        var root = Path.Combine(Path.GetTempPath(), "unmeasured-checks-" + Guid.NewGuid().ToString("N"));
        var store = new BatchMeasurementStore(root);
        var faults = new[]
        {
            "+1,+9.90000E+37,+9.90000E+37,+11",
            "2,1E-10,0.01,11",
            "0,1E-10,0.01,11",
            "0,9.9E37,9.9E37,2",
            "0,1E-10,9.9E37,2"
        };
        foreach (var raw in faults)
        {
            var reading = E4981AProtocol.ParseMeasurement(raw);
            Require(!reading.IsSuccessful, "Sentinel or contact failure was accepted as a reading.");
            var result = Invoke(null, "ClassifyE4981AMeasurement", reading)!;
            Require(!(bool)Property(result, "IsNg")! && !(bool)Property(result, "Passed")! &&
                ((string)Property(result, "ResultLabel")!).Contains("未测到"), "Missing reading was labeled OK/NG.");
            var states = (Array)Invoke(null, "CreateCarouselStationStates")!;
            var product = states.GetValue(5)!;
            Call(product, "SetLoaded");
            Call(product, "SetMeasurement", result);
            Require((bool)Property(product, "E4981AUnmeasured")! &&
                (string)Call(product, "GetUnloadDestination", true)! == "NG", "Missing reading must retain box-0 routing.");
            Call(product, "SetLoaded");
            Require(!(bool)Property(product, "E4981AUnmeasured")!, "New product inherited missing-reading state.");
            store.Append(new BatchMeasurement
            {
                BatchNumber = "QA", Instrument = "E4981A", MeasurementMode = "C", Unit = "nF",
                Value = reading.CapacitanceNf, DissipationFactor = reading.DissipationFactor,
                InstrumentStatus = reading.Status, RawResponse = raw, ValidReading = reading.IsSuccessful,
                Bin = "BIN0", Attempts = 3
            });
        }
        var records = store.ReadBatch("QA");
        Require(records.Count == faults.Length && records.Select(r => r.RawResponse).SequenceEqual(faults), "Raw responses were not retained.");
        Require(records.All(r => r.IsUnmeasured && !r.CountsAsNg && !r.CountsAsOk && !r.HasDistributionSample &&
            r.StationResult == "未测到" && r.ProductResult == "未测到" && r.Attempts == 3), "Stored missing readings entered statistics.");
        // Old data marked valid must also exclude the finite overflow sentinel.
        var legacy = records[0] with { ValidReading = true, InstrumentStatus = 0, StationPassed = true };
        Require(legacy.IsUnmeasured && !legacy.CountsAsOk, "Legacy overflow entered statistics.");
        var good = new BatchMeasurement { Instrument = "E4981A", Value = 1, DissipationFactor = 0.01,
            ValidReading = true, StationPassed = true, ProductPassedSoFar = true };
        var ng = good with { Value = 2, StationPassed = false, ProductPassedSoFar = false, Bin = "BIN0" };
        var timeout = new BatchMeasurement { Instrument = "SM7110", Value = 20, ValidReading = true, TimedOut = true };
        Require(ng.CountsAsNg && timeout.CountsAsNg && timeout.HasDistributionSample, "Real NG or SM7110 timeout was suppressed.");
        var rows = records.Concat(new[] { legacy, good, ng }).ToArray();
        var chart = new MeasurementDistributionChart();
        chart.SetData("QA", "nF", rows);
        var distribution = (MeasurementDistribution)typeof(MeasurementDistributionChart).GetField("_distribution", Private)!.GetValue(chart)!;
        Require(distribution.Count == 2 && distribution.Mean == 1.5, "Missing readings distorted the plotted distribution.");
        Require((int)typeof(MeasurementDistributionChart).GetField("_ng", Private)!.GetValue(chart)! == 1,
            "Chart counted missing readings as NG.");
        var query = new BatchQueryPage(store);
        typeof(BatchQueryPage).GetMethod("UpdateSummary", Private)!.Invoke(query, [rows]);
        string Metric(string name) => ((TextBlock)typeof(BatchQueryPage).GetField(name, Private)!.GetValue(query)!).Text;
        Require(Metric("_metricNg") == "1" && Metric("_metricYield") == 50d.ToString("0.0") + "%", "Query NG/yield included missing readings.");
        typeof(BatchQueryPage).GetMethod("UpdateSummary", Private)!.Invoke(query, [records]);
        Require(Metric("_metricNg") == "0" && Metric("_metricYield") == "—", "All-missing batch must have no yield or NG.");
        var export = Path.Combine(root, "records.csv");
        BatchMeasurementCsvExporter.Export(export, records);
        var csv = File.ReadAllText(export);
        Require(csv.Contains("未测到") && csv.Contains(faults[0]) && !csv.Contains(",NG,"), "Export lost missing status or original response.");
        Console.WriteLine("PASS: raw fault persistence, legacy sentinels, chart samples, NG/yield, CSV, box-0 routing and product state reset. No hardware opened.");
    }
}
