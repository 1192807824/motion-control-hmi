using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static void Main()
    {
        foreach (var status in new[] { 1, 2 })
        foreach (int? bin in new int?[] { null, 0, 1, 11 })
            CheckResult(new(status, 1e-10, 0.01, bin, ""), "BIN0", false);
        foreach (var bin in new[] { 0, 1, 2, 3, 4, 10, 11 })
            CheckResult(new(0, 1e-10, 0.01, bin, ""), bin is >= 1 and <= 3 ? $"BIN{bin}" : "BIN0", bin != 11);
        CheckResult(new(0, double.NaN, 0.01, null, ""), "BIN0", false);
        try
        {
            Invoke(null, "ClassifyE4981AMeasurement", new E4981AMeasurementResult(0, 1e-10, 0.01, null, ""));
            throw new Exception("Normal measurement without comparator BIN must remain a configuration error.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        Console.WriteLine("PASS: failed measurements with/without BIN continue as BIN0; good bins and configuration errors preserved.");

        // Bypass view startup and all device/settings initialization.
        var page = (HomePage)RuntimeHelpers.GetUninitializedObject(typeof(HomePage));
        var dwellInput = new TextBox { Text = "200" };
        Set(page, "TestStationDwellTextBox", dwellInput);
        Require((int)Invoke(page, "ReadTestStationDwellMilliseconds")! == 200, "Default dwell input not read.");
        foreach (var value in new[] { "0", "350", "60000" })
        {
            dwellInput.Text = value;
            Require((int)Invoke(page, "ReadTestStationDwellMilliseconds")! == int.Parse(value), "Custom dwell not read.");
        }
        foreach (var value in new[] { "", "-1", "60001", "1.5", "abc" })
        {
            dwellInput.Text = value;
            try
            {
                Invoke(page, "ReadTestStationDwellMilliseconds");
                throw new Exception("Invalid dwell was accepted.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        }
        dwellInput.Text = "200";
        var retryInput = new TextBox { Text = "1" };
        Set(page, "TestRetryCountTextBox", retryInput);
        foreach (var value in new[] { "0", "1", "3", "10" })
        {
            retryInput.Text = value;
            Require((int)Invoke(page, "ReadTestRetryCount")! == int.Parse(value), "Retry count not read.");
        }
        foreach (var value in new[] { "", "-1", "11", "1.5", "abc", "2147483648" })
        {
            retryInput.Text = value;
            try
            {
                Invoke(page, "ReadTestRetryCount");
                throw new Exception("Invalid retry count was accepted.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        }
        retryInput.Text = "1";
        Require(JsonSerializer.Deserialize<HomePageSettings>("{}")!.TestRetryCount == 1,
            "Old settings must default to one retry.");
        CheckRetriesAsync().GetAwaiter().GetResult();
        CheckE4981AFaultRetriesAsync().GetAwaiter().GetResult();
        CheckE4981ALossRoutingAsync().GetAwaiter().GetResult();
        CheckSM7110RangeAndRouting(page);
        CheckSM7110RangeRetriesAsync().GetAwaiter().GetResult();
        CheckSM7110TimedTestsAsync().GetAwaiter().GetResult();
        Require(JsonSerializer.Deserialize<HomePageSettings>("{}")!.TestStationDwellMilliseconds == 200,
            "Old settings must default to 200 ms.");
        var settingsPath = Path.Combine(Path.GetTempPath(), "test-station-dwell-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new HomePageSettingsStore(settingsPath);
            store.Save(new HomePageSettings
            {
                TestStationDwellMilliseconds = 350, TestRetryCount = 3,
                SM7110LowerLimit = -1e-12, SM7110UpperLimit = 2e-9, SM7110LimitMeasurementMode = "A"
            });
            Require(store.Load().TestStationDwellMilliseconds == 350, "Custom dwell not persisted.");
            Require(store.Load().TestRetryCount == 3, "Custom retry count not persisted.");
            Require(ProductRecipeStore.Clone(new ProductRecipe { Production = store.Load() })
                .Production.TestRetryCount == 3, "Recipe clone lost retry count.");
            var recipeSettings = ProductRecipeStore.Clone(new ProductRecipe { Production = store.Load() }).Production;
            Require(recipeSettings.SM7110LowerLimit == -1e-12 && recipeSettings.SM7110UpperLimit == 2e-9 &&
                    recipeSettings.SM7110LimitMeasurementMode == "A", "Range/units lost precision during save/load or recipe clone.");
            store.Save(new HomePageSettings { TestStationDwellMilliseconds = 0, TestRetryCount = 0 });
            Require(store.Load().TestStationDwellMilliseconds == 0, "Zero dwell must be preserved.");
            Require(store.Load().TestRetryCount == 0, "Disabled retries must be preserved.");
        }
        finally { File.Delete(settingsPath); }
        Console.WriteLine("PASS: dwell setting validation, old-file 200 ms default and save/load.");
        var canvas = new Canvas();
        var cards = Enumerable.Range(1, 16).Select(i =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = i.ToString() });
            panel.Children.Add(new TextBlock());
            panel.Children.Add(new TextBlock());
            var card = new Border { Child = panel };
            canvas.Children.Add(card);
            return card;
        }).ToArray();
        Set(page, "CarouselStationCanvas", canvas);
        Set(page, "CarouselSummaryText", new TextBlock());
        Set(page, "_testStationSettings", new Dictionary<int, TestStationSettings>
        {
            [5] = new() { Enabled = true, Instrument = TestStationInstrument.E4981A },
            [6] = new() { Enabled = true, Instrument = TestStationInstrument.SM7110 },
            [7] = new() { Enabled = false, Instrument = TestStationInstrument.None }
        });
        var states = (Array)Invoke(null, "CreateCarouselStationStates")!;
        var product = states.GetValue(2)!;
        product.GetType().GetMethod("SetLoaded")!.Invoke(product, null);
        Invoke(page, "UpdateCarouselStationDisplay", states, null, null);
        var positions = cards.Select(c => new Point(Canvas.GetLeft(c), Canvas.GetTop(c))).ToArray();
        for (var turn = 0; turn <= 16; turn++)
        {
            var station = (1 + turn) % 16 + 1;
            Require(ReferenceEquals(states.GetValue(station), product), "Product advanced to wrong physical station.");
            var count = (int)Invoke(page, "CountLoadedTestStations", states)!;
            Require(count == (station is 5 or 6 ? 1 : 0), "Test eligibility must match occupied enabled physical stations.");
            if (station == 5)
                product.GetType().GetMethod("SetTested")!.Invoke(product, new object[] { "BIN0" });
            Invoke(page, "UpdateCarouselStationDisplay", states, station == 5 ? new[] { 13 } : null, station == 5 ? "下压" : null);
            for (var i = 0; i < cards.Length; i++)
                Require(positions[i] == new Point(Canvas.GetLeft(cards[i]), Canvas.GetTop(cards[i])), "Fixed station card moved a second time.");
            var texts = ((StackPanel)cards[station - 1].Child).Children.OfType<TextBlock>().ToArray();
            Require(texts[1].Text != "空闲", "Product display does not match its physical position.");
            if (station == 5) Require(texts[1].Text == "下压", "Axis 13 must highlight physical station 5.");
            Invoke(page, "AdvanceCarouselOccupancy", states);
        }
        Require((string?)product.GetType().GetProperty("Bin")!.GetValue(product) == "BIN0", "Reject BIN lost during rotation.");
        Console.WriteLine("PASS: complete carousel revolution, enabled/empty stations, test-axis highlight, fixed station positions and BIN0 tracking. No hardware opened.");
        CheckSM7110SettingsView();
        CheckE4981ASetupChanges();
        CheckE4981ANanofaradUnits();
        CheckSM7110GigohmUnits();
        CheckDdTestStationInterlock();
    }

    private static void CheckResult(E4981AMeasurementResult input, string bin, bool passed)
    {
        var result = Invoke(null, "ClassifyE4981AMeasurement", input)!;
        Require((string?)result.GetType().GetProperty("Bin")!.GetValue(result) == bin, "Wrong destination BIN.");
        Require((bool)result.GetType().GetProperty("Passed")!.GetValue(result)! == passed, "Wrong pass/fail result.");
        Require((bool)Property(result, "IsNg")! == (bin == "BIN0"), "BIN0 must be NG independently of measurement validity.");
        Require((string)Property(result, "ResultLabel")! == (bin == "BIN0" ? "NG · BIN0" : bin), "E4981A badge must label BIN0 as NG.");
    }

    private static async Task CheckRetriesAsync()
    {
        foreach (var limit in new[] { 0, 1, 3, 10 })
        {
            var attempts = 0;
            var retries = new List<int>();
            var passed = await Retry(_ => { attempts++; return Task.FromResult(false); }, limit,
                (number, _) => { retries.Add(number); return Task.CompletedTask; });
            Require(!passed && attempts == limit + 1, "Failed results must exhaust exactly N extra attempts.");
            Require(retries.SequenceEqual(Enumerable.Range(1, limit)), "Wrong retry progress numbers.");
        }

        foreach (var successAt in new[] { 1, 2, 4 })
        {
            var attempts = 0;
            var passed = await Retry(_ => Task.FromResult(++attempts == successAt), 3);
            Require(passed && attempts == successAt, "Successful measurement must stop retrying immediately.");
        }

        var calls = 0;
        Require(await Retry(_ => ++calls == 1
                ? Task.FromException<bool>(new TimeoutException("transient")) : Task.FromResult(true), 2)
            && calls == 2, "Transient communication failure was not retried.");
        calls = 0;
        var failure = new IOException("disconnected");
        try
        {
            await Retry(_ => { calls++; return Task.FromException<bool>(failure); }, 2);
            throw new Exception("Exhausted communication failure must propagate.");
        }
        catch (IOException ex) when (ReferenceEquals(ex, failure)) { }
        Require(calls == 3, "Communication retries exceeded limit.");

        using var cancellation = new CancellationTokenSource();
        calls = 0;
        try
        {
            await Retry(_ => { calls++; return Task.FromResult(false); }, 3,
                (_, token) => { cancellation.Cancel(); return Task.Delay(100, token); }, cancellation.Token);
            throw new Exception("Cancellation during retry wait must propagate.");
        }
        catch (OperationCanceledException) { }
        Require(calls == 1, "Cancellation started another instrument measurement.");
        try
        {
            await Retry(_ => { calls++; return Task.FromResult(true); }, 3, token: cancellation.Token);
            throw new Exception("Already canceled measurement must not run.");
        }
        catch (OperationCanceledException) { }
        Require(calls == 1, "Already canceled token triggered an instrument.");
        calls = 0;
        try
        {
            await Retry(_ => { calls++; return Task.FromException<bool>(new OperationCanceledException()); }, 3);
            throw new Exception("Instrument cancellation must propagate.");
        }
        catch (OperationCanceledException) { }
        Require(calls == 1, "Instrument cancellation was retried.");

        var capacitanceResults = new Queue<E4981AMeasurementResult>(new[]
        {
            new E4981AMeasurementResult(2, 1e-10, 0.01, null, ""),
            new E4981AMeasurementResult(1, 1e-10, 0.01, 0, ""),
            new E4981AMeasurementResult(0, 1e-10, 0.01, 2, "")
        });
        var resistanceCalls = 0;
        object? finalCapacitanceResult = null;
        var results = await Task.WhenAll(
            Retry(_ =>
            {
                finalCapacitanceResult = Invoke(null, "ClassifyE4981AMeasurement", capacitanceResults.Dequeue())!;
                return Task.FromResult((bool)finalCapacitanceResult.GetType().GetProperty("Passed")!
                    .GetValue(finalCapacitanceResult)!);
            }, 3, async (_, _) => await Task.Yield()),
            Retry(_ =>
            {
                resistanceCalls++;
                return Task.FromResult(new SM7110MeasurementResult(1e9, 0, "R", "").IsSuccessful);
            }, 3));
        Require(results.All(passed => passed) && capacitanceResults.Count == 0 && resistanceCalls == 1,
            "A failed E4981A must retry independently without retesting a successful SM7110.");
        Require((string?)finalCapacitanceResult!.GetType().GetProperty("Bin")!
            .GetValue(finalCapacitanceResult) == "BIN2", "Recovered E4981A must use the final successful BIN.");
        resistanceCalls = 0;
        Require(await Retry(_ => Task.FromResult(new SM7110MeasurementResult(
                1e9, ++resistanceCalls == 1 ? 5 : 0, "R", "").IsSuccessful), 2)
            && resistanceCalls == 2, "SM7110 contact failure must retry and stop on recovery.");
        calls = 0;
        Require(await Retry(_ =>
        {
            calls++;
            return Task.FromResult((bool)Property(Invoke(null, "ClassifyE4981AMeasurement",
                new E4981AMeasurementResult(0, 1e-10, 0.01, 0, ""))!, "Passed")!);
        }, 3) && calls == 1, "A valid BIN0 is a classification, not a reason to retry.");
        Console.WriteLine("PASS: retry limits, early success, communication recovery/exhaustion, cancellation and settings validation.");
    }

    private static async Task CheckE4981AFaultRetriesAsync()
    {
        // Exercise the wire parser, production classification and retry runner together.
        foreach (var response in new[]
        {
            "1,+9.9E37,+9.9E37",       // OVLD without comparator result
            "1,+9.9E37,+9.9E37,1",     // Failure status overrides an apparent good BIN
            "2,+1.0E-10,+0.01,0",      // Low C / NC
            "2,+1.0E-10,+0.01,11",
            "0,+9.9E37,+9.9E37,11",    // Fault reported by comparator only
            "0,+1.0E-10,+0.01,11"      // BIN11 is a fault even with finite, plausible numbers
        })
        {
            var failed = E4981AProtocol.ParseMeasurement(response);
            Require(!failed.IsSuccessful && failed.StatusDescription != "测量正常",
                "Overload/no-contact must not be reported as a normal measurement.");
            foreach (var retryCount in new[] { 0, 1, 3 })
            foreach (var recover in new[] { false, true })
            {
                var attempts = 0;
                object? finalResult = null;
                var success = await Retry(_ =>
                {
                    attempts++;
                    var raw = recover && attempts == 2 ? "0,+1.0E-10,+0.01,2" : response;
                    finalResult = Invoke(null, "ClassifyE4981AMeasurement", E4981AProtocol.ParseMeasurement(raw))!;
                    return Task.FromResult((bool)Property(finalResult, "Passed")!);
                }, retryCount);
                var recovered = recover && retryCount > 0;
                Require(success == recovered && attempts == (recovered ? 2 : retryCount + 1),
                    $"Wrong overload/no-contact retry outcome or count for {response}.");
                Require((string?)Property(finalResult!, "Bin") == (recovered ? "BIN2" : "BIN0"),
                    "Final overload/no-contact result did not follow existing BIN handling.");
            }
        }
        Console.WriteLine("PASS: E4981A OVLD, Low C/NC and status-0 BIN11 retry through wire parsing; recovery, exhaustion and retries disabled.");
    }

    private static async Task CheckE4981ALossRoutingAsync()
    {
        var settings = new TcpConnectionSettings
        {
            ComparatorEnabled = true, LossLimitEnabled = true, LossLower = 50, LossUpper = 750,
            Bin1LowerPf = -9999, Bin1UpperPf = 0, Bin2LowerPf = 0, Bin2UpperPf = 600000,
            Bin3LowerPf = 600000, Bin3UpperPf = 1200000
        };
        var screenshot = new E4981AMeasurementResult(0, 408873e-12, 0.126388, 10, "");
        var judged = E4981AProtocol.ApplyLossLimit(screenshot, settings);
        Require(judged.IsSuccessful && judged.LossRejected && judged.LossFailureReason!.Contains("低于下限50"),
            "Screenshot: valid measurement can still be NG because 0.126388 < 50.");
        Require(E4981AProtocol.BuildSetupCommands(settings).Contains("CALC1:COMP:SEC:LIM 50,750"),
            "Loss limits must use raw D units without hidden percent conversion.");
        foreach (var d in new[] { 49.9, 50, 100, 750, 750.1, double.NaN, double.PositiveInfinity })
        {
            var result = E4981AProtocol.ApplyLossLimit(screenshot with { DissipationFactor = d, Bin = 2 }, settings);
            Require(result.LossRejected == !(d >= 50 && d <= 750), "Loss range boundaries/invalid values handled incorrectly.");
        }
        var range = CreateNested("SM7110AcceptanceRange", 100d, 200d, "R");
        foreach (var instrumentBin in new[] { 0, 1, 2, 3, 10 })
        {
            var e4981a = Invoke(null, "ClassifyE4981AMeasurement",
                E4981AProtocol.ApplyLossLimit(screenshot with { Bin = instrumentBin }, settings))!;
            var product = ((Array)Invoke(null, "CreateCarouselStationStates")!).GetValue(1)!;
            Call(product, "SetMeasurement", e4981a);
            Require((string)Call(product, "GetUnloadDestination", false)! == "NG",
                "E4981A loss failure must go to box0 even without SM7110.");
            ExpectFailure<InvalidOperationException>(() => Call(product, "GetUnloadDestination", true));
            var originalBin = (string)Property(product, "Bin")!;
            var sm7110 = Invoke(null, "ClassifySM7110Measurement",
                new SM7110MeasurementResult(150, 0, "R", ""), originalBin, range)!;
            Call(product, "SetMeasurement", sm7110);
            Require((bool)Property(product, "E4981ALossRejected")! &&
                    (string)Call(product, "GetUnloadDestination", true)! == "NG",
                "SM7110 OK must never erase an earlier E4981A loss failure or stop BIN0 NG as unclassified.");
            Call(product, "SetLoaded");
            Require(!(bool)Property(product, "E4981ALossRejected")! && Property(product, "E4981ALossFailureReason") is null,
                "Loss failure leaked into a new product.");
        }
        settings.LossLimitEnabled = false;
        var disabled = E4981AProtocol.ApplyLossLimit(screenshot with { Bin = 2 }, settings);
        Require(!disabled.LossRejected && (bool)Property(Invoke(null, "ClassifyE4981AMeasurement", disabled)!, "Passed")!,
            "Disabled loss filter must preserve valid BIN2.");
        settings.LossLimitEnabled = true;
        settings.LossLower = 0.05;
        settings.LossUpper = 0.75;
        var valid = E4981AProtocol.ApplyLossLimit(screenshot with { Bin = 2 }, settings);
        Require(!valid.LossRejected && (string?)Property(Invoke(null, "ClassifyE4981AMeasurement", valid)!, "Bin") == "BIN2",
            "Screenshot capacitance and D must pass BIN2 when both configured ranges contain the values.");
        foreach (var recover in new[] { false, true })
        {
            var attempts = 0;
            object? final = null;
            await Retry(_ =>
            {
                attempts++;
                final = Invoke(null, "ClassifyE4981AMeasurement", E4981AProtocol.ApplyLossLimit(
                    screenshot with { Bin = 2, DissipationFactor = recover && attempts == 2 ? 0.126388 : 0.9 }, settings))!;
                return Task.FromResult((bool)Property(final, "Passed")!);
            }, 2);
            var product = ((Array)Invoke(null, "CreateCarouselStationStates")!).GetValue(1)!;
            Call(product, "SetMeasurement", final!);
            Require(attempts == (recover ? 2 : 3) &&
                    (string)Call(product, "GetUnloadDestination", false)! == (recover ? "BIN2" : "NG"),
                "Final loss retry result must determine BIN2 versus box0 NG.");
        }
        Console.WriteLine("PASS: screenshot loss diagnosis, raw D units, inclusive limits, disabled filter, loss retries and sticky NG across both stations.");
    }

    private static void CheckSM7110RangeAndRouting(HomePage page)
    {
        var lower = new TextBox { Text = "1e9" };
        var upper = new TextBox { Text = "2e9" };
        var mode = new ComboBox { ItemsSource = new[] { "R", "A", "RS", "RV", "RL" }, SelectedItem = "A" };
        Set(page, "SM7110LowerLimitTextBox", lower);
        Set(page, "SM7110UpperLimitTextBox", upper);
        Set(page, "SM7110LimitModeComboBox", mode);
        var range = Invoke(page, "ReadSM7110AcceptanceRange")!;
        foreach (var values in new[] { ("", "2"), ("1", ""), ("NaN", "2"), ("1", "Infinity"), ("3", "2"), ("abc", "2") })
        {
            lower.Text = values.Item1;
            upper.Text = values.Item2;
            ExpectFailure<ArgumentException>(() => Invoke(page, "ReadSM7110AcceptanceRange"));
        }
        lower.Text = "0";
        upper.Text = "0";
        _ = Invoke(page, "ReadSM7110AcceptanceRange");
        lower.Text = "-1e-12";
        upper.Text = "1e-12";
        mode.SelectedItem = "A";
        var currentRange = Invoke(page, "ReadSM7110AcceptanceRange")!;
        Require((double)Property(currentRange, "Lower")! == -1e-12, "Scientific notation lost precision.");
        ExpectFailure<InvalidOperationException>(() => Invoke(null, "ClassifySM7110Measurement",
            new SM7110MeasurementResult(0, 0, "R", ""), "BIN1", currentRange));
        var oldSettings = JsonSerializer.Deserialize<HomePageSettings>("{}")!;
        Require(oldSettings.SM7110LowerLimit is null && oldSettings.SM7110UpperLimit is null,
            "Old files must require range configuration before dual-station operation.");

        foreach (var value in new[] { 1e9 - 1, 1e9, 1.5e9, 2e9, 2e9 + 1, double.NaN, double.PositiveInfinity })
        {
            var result = Invoke(null, "ClassifySM7110Measurement", new SM7110MeasurementResult(value, 0, "A", ""), "BIN2", range)!;
            Require((bool)Property(result, "Passed")! == (value >= 1e9 && value <= 2e9), "Wrong inclusive range decision.");
        }
        foreach (var status in new[] { 1, 3, 5, 7, 9 })
        {
            var result = Invoke(null, "ClassifySM7110Measurement", new SM7110MeasurementResult(1.5e9, status, "A", ""), "BIN2", range)!;
            Require(!(bool)Property(result, "Passed")!, "Instrument failure must not pass even when value lies within range.");
        }

        var positions = CreateNested("BinDropPositions",
            CreateNested("BinDropPosition", 100d, 200d), CreateNested("BinDropPosition", 300d, 400d),
            CreateNested("BinDropPosition", 500d, 600d), CreateNested("BinDropPosition", 700d, 800d));
        foreach (var bin in new[] { "BIN0", "BIN1", "BIN2", "BIN3" })
        {
            var product = ((Array)Invoke(null, "CreateCarouselStationStates")!).GetValue(1)!;
            Call(product, "SetLoaded");
            ExpectFailure<InvalidOperationException>(() => Call(product, "GetUnloadDestination", false));
            Call(product, "SetTested", bin);
            Require((string)Call(product, "GetUnloadDestination", false)! == (bin == "BIN0" ? "NG" : bin),
                "Single station must route out-of-bin capacitance to NG and preserve BIN1-3.");
            ExpectFailure<InvalidOperationException>(() => Call(product, "GetUnloadDestination", true));
            foreach (var passed in new[] { false, true })
            {
                var result = Invoke(null, "ClassifySM7110Measurement",
                    new SM7110MeasurementResult(passed ? 1.5e9 : 3e9, 0, "A", ""), bin, range)!;
                Call(product, "SetMeasurement", result);
                Require((string?)Property(product, "Bin") == bin, "SM7110 must retain the original E4981A BIN.");
                Require((bool)Property(product, "SM7110Passed")! == passed, "Final SM7110 judgement lost.");
                var rejected = !passed || bin == "BIN0";
                Require((bool)Property(result, "IsNg")! == rejected, "BIN0 must remain NG even when SM7110 passes.");
                Require((string)Property(result, "ResultLabel")! == (rejected ? "NG · BIN0" : $"OK · {bin}"),
                    "Result badge must identify the actual NG box and never show OK BIN0.");
                Require((bool)Property(result, "Passed")! == passed, "Prior BIN0 must not cause a successful SM7110 reading to retry.");
                if (passed && bin == "BIN0")
                    Require(((string)Property(result, "StatusDescription")!).Contains("整体NG"), "BIN0 status must show overall NG.");
                var destination = (string)Call(product, "GetUnloadDestination", true)!;
                Require(destination == (rejected ? "NG" : bin), "NG must override every original BIN.");
                var target = Call(positions, "Resolve", destination, 13)!;
                var expected = Property(positions, rejected ? "Bin0" : "Bin" + bin[^1]);
                Require(target.Equals(expected), "Logical destination mapped to the wrong physical box coordinates.");
            }
            Call(product, "SetLoaded");
            Require(Property(product, "Bin") is null && Property(product, "SM7110Passed") is null &&
                    Property(product, "SM7110Value") is null, "New product inherited a prior measurement.");
        }
        Console.WriteLine("PASS: inclusive SM7110 range, invalid/missing limits, units, four-box routing, pending results and BIN0 NG precedence.");
    }

    private static async Task CheckSM7110RangeRetriesAsync()
    {
        var range = CreateNested("SM7110AcceptanceRange", 100d, 200d, "A");
        foreach (var recovery in new[] { false, true })
        {
            object? finalResult = null;
            var attempts = 0;
            var passed = await Retry(_ =>
            {
                attempts++;
                finalResult = Invoke(null, "ClassifySM7110Measurement",
                    new SM7110MeasurementResult(recovery && attempts == 2 ? 150 : 250, 0, "A", ""), "BIN3", range)!;
                return Task.FromResult((bool)Property(finalResult, "Passed")!);
            }, 2);
            var product = ((Array)Invoke(null, "CreateCarouselStationStates")!).GetValue(1)!;
            Call(product, "SetMeasurement", finalResult!);
            Require(passed == recovery && attempts == (recovery ? 2 : 3), "Range failure retry outcome/count incorrect.");
            Require((string)Call(product, "GetUnloadDestination", true)! == (recovery ? "BIN3" : "NG"),
                "Final range retry result did not determine physical routing.");
        }
        Console.WriteLine("PASS: out-of-range retries recover to original BIN or exhaust to NG.");
    }

    private static object CreateNested(string name, params object[] args) =>
        Activator.CreateInstance(typeof(HomePage).GetNestedType(name, BindingFlags.NonPublic)!, args)!;

    private static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name)!.Invoke(target, args);

    private static void ExpectFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is T) { return; }
        throw new Exception($"Expected {typeof(T).Name} was not raised.");
    }

    private static void CheckSM7110SettingsView()
    {
        _ = new Application();
        // Views only, with every subsequent save redirected away from the machine's settings.
        var home = new HomePage();
        var connection = new ConnectionConfigPage();
        home.AttachConnectionConfigController(connection);
        var rangeCard = (Border)home.FindName("SM7110RangeCard");
        Require(ReferenceEquals(((ContentControl)connection.FindName("SM7110AcceptanceRangeHost")).Content, rangeCard),
            "Acceptance range must be hosted by the SM7110 connection page.");
        Require(rangeCard.Visibility == Visibility.Visible, "Acceptance range is hidden in the connection page.");
        var directory = Path.Combine(AppContext.BaseDirectory, "ui-checks");
        Directory.CreateDirectory(directory);
        var store = new HomePageSettingsStore(Path.Combine(directory, "home.json"));
        Set(home, "_homeSettingsStore", store);
        var single = new HomePageSettings
        {
            Bin0PositionX = 100, Bin0PositionY = 200,
            TestStationSettings = new()
            {
                [5] = new() { Enabled = true, Instrument = TestStationInstrument.E4981A },
                [6] = new() { Enabled = false, Instrument = TestStationInstrument.None },
                [7] = new() { Enabled = false, Instrument = TestStationInstrument.None }
            }
        };
        home.ApplyRecipeSettings(single);
        Require(((TextBlock)home.FindName("Bin0PositionLabel")).Text == "0号盒 · NG（BIN0）", "Single-station box label incorrect.");
        Require(!(bool)Invoke(home, "IsSM7110TestEnabled")!, "Single-station configuration unexpectedly enabled SM7110.");
        var savedSingle = home.CaptureRecipeSettings();
        Require(savedSingle.SM7110LowerLimit is null, "Single-station recipe must not require SM7110 limits.");

        var dual = ProductRecipeStore.Clone(savedSingle);
        dual.TestStationSettings[6] = new() { Enabled = true, Instrument = TestStationInstrument.SM7110 };
        home.ApplyRecipeSettings(dual);
        Require(((TextBlock)home.FindName("Bin0PositionLabel")).Text == "0号盒 · NG（BIN0）", "Dual-station box label incorrect.");
        Require(((TextBlock)home.FindName("Bin3PositionLabel")).Text == "3号盒 · OK · BIN3", "Good-bin label incorrect.");
        Require(!(bool)Invoke(home, "AllTestStationParametersValid")!, "Dual mode with missing limits must block start.");

        var mode = (ComboBox)home.FindName("SM7110LimitModeComboBox");
        var lower = (TextBox)home.FindName("SM7110LowerLimitTextBox");
        var upper = (TextBox)home.FindName("SM7110UpperLimitTextBox");
        mode.SelectedValue = "A";
        lower.Text = "-1e-12";
        upper.Text = "2e-9";
        Require((bool)Invoke(home, "AllTestStationParametersValid")!, "Valid dual mode range must pass validation.");
        Require(store.Load().SM7110LowerLimit == -1e-12 && store.Load().SM7110UpperLimit == 2e-9 &&
                store.Load().SM7110LimitMeasurementMode == "A", "Real input events failed to auto-save range and units.");
        var snapshot = Invoke(home, "ReadSM7110AcceptanceRange")!;
        var savedDual = home.CaptureRecipeSettings();
        lower.Text = "3e-9";
        Require(!(bool)Invoke(home, "AllTestStationParametersValid")!, "Inverted limits must block start.");
        try { home.CaptureRecipeSettings(); throw new Exception("Invalid range was silently captured."); }
        catch (ArgumentException) { }
        Require(store.Load().SM7110LowerLimit == -1e-12, "Invalid input overwrote valid saved limits.");
        home.ApplyRecipeSettings(savedSingle);
        home.ApplyRecipeSettings(savedDual);
        Require((double)Property(Invoke(home, "ReadSM7110AcceptanceRange")!, "Lower")! == -1e-12,
            "Recipe switch rounded a small limit to zero.");
        Require(store.Load().Bin0PositionX == 100 && store.Load().Bin0PositionY == 200,
            "Switching modes changed physical box coordinates.");
        upper.Text = "3e-9";
        Require((double)Property(snapshot, "Upper")! == 2e-9, "Production snapshot changed after editing parameters.");

        var parameters = new ParameterSettingsPage();
        parameters.AttachSettingsContent(home.DetachParameterSettingsPanel());
        Require(!IsLogicalDescendant(rangeCard, parameters) && IsLogicalDescendant(rangeCard, connection),
            "Acceptance range must appear only in instrument connection settings.");
        parameters.Measure(new Size(1440, 900));
        parameters.Arrange(new Rect(0, 0, 1440, 900));
        parameters.UpdateLayout();
        RenderCard(lower, Path.Combine(directory, "sm7110-range.png"));
        RenderCard((TextBlock)home.FindName("Bin0PositionLabel"), Path.Combine(directory, "dual-mode-boxes.png"));
        Require(lower.ActualWidth > 150 && lower.ActualHeight >= 30, "Range inputs are too small in the connection layout.");
        Console.WriteLine("PASS: connection-only range editor, real WPF controls, mode labels, start validation, auto-save, recipe reload and frozen limits. No hardware attached.");
        Console.WriteLine("UI artifacts: " + directory);
    }

    private static void RenderCard(FrameworkElement input, string path)
    {
        DependencyObject? parent = input;
        while (parent is not Border && parent is not null)
            parent = LogicalTreeHelper.GetParent(parent);
        var card = (Border)parent!;
        if (card.Parent is Panel panel)
            panel.Children.Remove(card);
        else if (card.Parent is ContentControl host)
            host.Content = null;
        card.Margin = new Thickness(0);
        var preview = new Border { Child = card, Width = 630, Background = Brushes.Black };
        preview.Measure(new Size(630, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, 630, preview.DesiredSize.Height));
        preview.UpdateLayout();
        var bitmap = new RenderTargetBitmap(630, (int)Math.Ceiling(preview.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(preview);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool IsLogicalDescendant(DependencyObject child, DependencyObject ancestor)
    {
        for (var parent = LogicalTreeHelper.GetParent(child); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }

    private static Task<bool> Retry(
        Func<CancellationToken, Task<bool>> measure,
        int count,
        Func<int, CancellationToken, Task>? beforeRetry = null,
        CancellationToken token = default) =>
        (Task<bool>)typeof(HomePage).Assembly
            .GetType("ControlHub.Services.Devices.TestMeasurementRetry")!
            .GetMethod("ExecuteAsync")!.MakeGenericMethod(typeof(bool))
            .Invoke(null, new object[]
            {
                measure, (Func<bool, bool>)(passed => passed), count,
                beforeRetry ?? ((_, _) => Task.CompletedTask), token
            })!;

    private static object? Invoke(object? target, string name, params object?[] args) =>
        typeof(HomePage).GetMethod(name, Private)!.Invoke(target, args);

    private static void Set(object target, string name, object value) =>
        typeof(HomePage).GetField(name, Private)!.SetValue(target, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
