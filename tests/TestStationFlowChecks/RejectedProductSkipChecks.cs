using System.Runtime.CompilerServices;
using System.Windows.Controls;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckRejectedProductSkip()
    {
        // No device controllers or persistence: reaching any motion/measurement path fails this check.
        var page = (HomePage)RuntimeHelpers.GetUninitializedObject(typeof(HomePage));
        var settings = new Dictionary<int, TestStationSettings>
        {
            [5] = new() { Enabled = true, Instrument = TestStationInstrument.E4981A },
            [6] = new() { Enabled = true, Instrument = TestStationInstrument.SM7110 },
            [7] = new() { Enabled = false, Instrument = TestStationInstrument.None }
        };
        Set(page, "_testStationSettings", settings);
        Set(page, "StartProductionHintText", new TextBlock());
        foreach (var prefix in new[] { "Station2", "Station3" })
        foreach (var suffix in new[] { "StatusText", "StateBadgeText", "ResultText" })
            Set(page, prefix + suffix, new TextBlock());

        foreach (var smStation in new[] { 6, 7 })
        {
            settings[6] = new() { Enabled = smStation == 6, Instrument = TestStationInstrument.SM7110 };
            settings[7] = new() { Enabled = smStation == 7, Instrument = TestStationInstrument.SM7110 };
            foreach (var lossRejected in new[] { false, true })
            {
                var states = (Array)Invoke(null, "CreateCarouselStationStates")!;
                var product = states.GetValue(smStation)!;
                Call(product, "SetLoaded");
                var productId = Property(product, "ProductId");
                var reading = new E4981AMeasurementResult(0, 1e-10, 0.01, lossRejected ? 2 : 0, "")
                {
                    LossFailureReason = lossRejected ? "损耗超限" : null
                };
                Call(product, "SetMeasurement", Invoke(null, "ClassifyE4981AMeasurement", reading)!);
                var required = (IReadOnlyDictionary<int, int>)Invoke(page, "GetRequiredTestStationAxisByStation", states)!;
                Require(required.Count == 0, "Final first-station NG must not press or measure at SM7110.");
                var task = (Task<int>)Invoke(page, "RunOccupiedTestStationsAsync", states, CancellationToken.None)!;
                Require(task.GetAwaiter().GetResult() == 0, "All-skipped cycle must finish without hardware or instruments.");
                var badge = (TextBlock)typeof(HomePage).GetField($"Station{smStation - 4}StateBadgeText", Private)!.GetValue(page)!;
                Require(badge.Text == "已跳过", "Skipped station must not retain a previous product's measurement badge.");
                Require(Property(product, "SM7110Passed") is null && Property(product, "SM7110Value") is null,
                    "Skipping must not fabricate an SM7110 judgement or measurement value.");
                Require(Equals(productId, Property(product, "ProductId")) &&
                        (string)Call(product, "GetUnloadDestination", true)! == "NG",
                    "Skipped product must retain its identity and go directly to the NG box.");

                var upstream = states.GetValue(5)!;
                Call(upstream, "SetLoaded");
                required = (IReadOnlyDictionary<int, int>)Invoke(page, "GetRequiredTestStationAxisByStation", states)!;
                Require(required.Count == 1 && required[5] == 13,
                    "A downstream reject must not suppress another product's first-station test.");

                // A successful final retry must still continue through SM7110.
                Call(product, "SetMeasurement", Invoke(null, "ClassifyE4981AMeasurement",
                    new E4981AMeasurementResult(0, 1e-10, 0.01, 2, ""))!);
                required = (IReadOnlyDictionary<int, int>)Invoke(page, "GetRequiredTestStationAxisByStation", states)!;
                Require(required.Count == 2 && required[smStation] == smStation + 8,
                    "Recovered/BIN1-3 products must still press and measure at SM7110.");
                ExpectFailure<InvalidOperationException>(() => Call(product, "GetUnloadDestination", true));

                Call(product, "SetLoaded");
                Require(!(bool)Property(product, "HasFinalE4981ARejection")!, "New product inherited a reject decision.");
                ExpectFailure<InvalidOperationException>(() => Call(product, "GetUnloadDestination", true));
                Call(product, "SetTested", "BIN?");
                Require(!(bool)Property(product, "HasFinalE4981ARejection")!, "Invalid BIN must not qualify for skipping.");
                ExpectFailure<InvalidOperationException>(() => Call(product, "GetUnloadDestination", true));
            }
        }
        Console.WriteLine("PASS: final BIN0/loss NG skips SM7110 motion and measurement, preserves null readings, routes to NG; mixed products, station assignment, recovered retries and missing results preserved.");
    }
}
