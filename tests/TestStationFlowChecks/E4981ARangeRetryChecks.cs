using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckE4981ARangeRetry()
    {
        var home = new HomePage();
        var directory = Path.Combine(AppContext.BaseDirectory, "ui-checks");
        var store = new HomePageSettingsStore(Path.Combine(directory, "e4981a-range.json"));
        Set(home, "_homeSettingsStore", store);
        var legacy = JsonSerializer.Deserialize<HomePageSettings>("""{"TestRetryCount":1,"E4981ARangeRetryCount":9}""")!;
        legacy.TestStationSettings = new()
        {
            [5] = new() { Enabled = true, Instrument = TestStationInstrument.E4981A },
            [6] = new() { Enabled = false, Instrument = TestStationInstrument.None },
            [7] = new() { Enabled = false, Instrument = TestStationInstrument.None }
        };
        home.ApplyRecipeSettings(legacy);
        var lower = (TextBox)home.FindName("E4981ARetryLowerTextBox");
        var upper = (TextBox)home.FindName("E4981ARetryUpperTextBox");
        var count = (TextBox)home.FindName("TestRetryCountTextBox");
        Require(count.Text == "1" && lower.Text == "" && upper.Text == "" &&
                !JsonSerializer.Serialize(legacy).Contains("E4981ARangeRetryCount"),
            "Legacy recipes must retain the original retry count and discard the separate range budget.");
        lower.Text = "1";
        upper.Text = "1000";
        count.Text = "3";
        var saved = store.Load();
        Require(saved.E4981ARetryLowerNf == 1 && saved.E4981ARetryUpperNf == 1000 && saved.TestRetryCount == 3,
            "Actual range input events must persist all three parameters.");
        var snapshot = Invoke(home, "ReadE4981ARetryRange")!;
        var recipe = home.CaptureRecipeSettings();
        var recipeStore = new ProductRecipeStore(Path.Combine(directory, "e4981a-recipes"));
        var product = recipeStore.Create("区间复测");
        product.Production = recipe;
        recipeStore.Save(product);
        foreach (var invalid in new[] { "", "-1", "11", "1.5", "abc", "2147483648" })
        {
            count.Text = invalid;
            ExpectFailure<ArgumentException>(() => Invoke(home, "ReadE4981ARetryRange"));
            Require(!(bool)Invoke(home, "AllTestStationParametersValid")!, "Invalid count must block production start.");
            Require(store.Load().TestRetryCount == 3, "Invalid count overwrote the last saved value.");
        }
        count.Text = "3";
        foreach (var invalid in new[] { "", "1001", "NaN", "Infinity", "abc" })
        {
            lower.Text = invalid;
            ExpectFailure<ArgumentException>(() => Invoke(home, "ReadE4981ARetryRange"));
            try { home.CaptureRecipeSettings(); throw new Exception("Invalid range saved into recipe."); }
            catch (ArgumentException) { }
            Require(!(bool)Invoke(home, "AllTestStationParametersValid")!, "Invalid range must block production start.");
            Require(store.Load().E4981ARetryLowerNf == 1, "Invalid range overwrote the saved lower limit.");
        }
        home.ApplyRecipeSettings(legacy);
        home.ApplyRecipeSettings(new ProductRecipeStore(Path.Combine(directory, "e4981a-recipes"))
            .LoadAll().Single(r => r.Id == product.Id).Production);
        Require(lower.Text == "1" && upper.Text == "1000" && count.Text == "3",
            "Recipe switch/reload must restore all range fields.");
        Set(home, "_startSequenceRunning", true);
        Invoke(home, "UpdateHomeCommandState");
        Require(!lower.IsEnabled && !upper.IsEnabled && !count.IsEnabled, "Production must lock the range editors.");
        Set(home, "_startSequenceRunning", false);
        Invoke(home, "UpdateHomeCommandState");
        upper.Text = "2000";
        Require((double?)Property(snapshot, "UpperNf") == 1000, "Editing mutated the production snapshot.");
        home.ApplyRecipeSettings(recipe);
        var parameters = new ParameterSettingsPage();
        parameters.AttachSettingsContent(home.DetachParameterSettingsPanel());
        Require(IsLogicalDescendant(lower, parameters), "Range editor must appear in parameter configuration.");
        Require(IsLogicalDescendant(count, parameters) && home.FindName("E4981ARangeRetryCountTextBox") is null,
            "Parameter configuration must expose only one retry count.");
        RenderCard(lower, Path.Combine(directory, "e4981a-range-retry.png"));
        Require(lower.ActualWidth > 150 && lower.ActualHeight >= 30, "Range input is clipped or too small.");
        CheckE4981ARangeRetryFlowAsync(snapshot).GetAwaiter().GetResult();
        Console.WriteLine("PASS: E4981A outside-range retries, one shared budget, boundaries, final routing, cancellation, single-count UI, validation, auto-save, recipe disk reload and frozen configuration.");
    }

    private static async Task CheckE4981ARangeRetryFlowAsync(object range)
    {
        // Use the actual production predicate, including loss/fault classification.
        bool Complete(E4981AMeasurementResult reading) =>
            (bool)Invoke(null, "IsTestStationMeasurementComplete", Invoke(null, "ClassifyE4981AMeasurement", reading), range)!;
        static E4981AMeasurementResult Reading(double nf, int bin = 2) => new(0, nf / 1e9, 0.01, bin, "");
        foreach (var nf in new[] { 1d, 450, 1000 })
            Require(Complete(Reading(nf)), "Valid range must include both boundaries without retrying.");
        foreach (var nf in new[] { 0.05, 0.999, 1000.001, double.NaN, double.PositiveInfinity })
            Require(!Complete(Reading(nf)), "Outside/invalid reading must request a retry.");
        Require(!Complete(Reading(450, 11)) && !Complete(Reading(450) with { Status = 2 }),
            "Instrument faults must request a retry from the same budget.");

        async Task<(E4981AMeasurementResult Final, int Calls, int Motions)> Run(
            E4981AMeasurementResult[] readings, int retryCount)
        {
            var calls = 0;
            var motions = 0;
            var final = await RangeRetry(_ => Task.FromResult(readings[Math.Min(calls++, readings.Length - 1)]),
                Complete, retryCount,
                (number, _) => { Require(number == ++motions, "Retry sequence skipped or duplicated a mechanical cycle."); return Task.CompletedTask; });
            return (final, calls, motions);
        }
        foreach (var limit in new[] { 0, 1, 3, 10 })
        {
            var result = await Run([Reading(0.05)], limit);
            Require(result.Calls == limit + 1 && result.Motions == limit, "Range retries did not exhaust exactly N extra measurements.");
            var classified = Invoke(null, "ClassifyE4981AMeasurement", result.Final)!;
            Require((string?)Property(classified, "Bin") == "BIN2" && !(bool)Property(classified, "IsNg")!,
                "Exhausted range retries must retain the last normal BIN classification.");
        }
        var recovered = await Run([Reading(0.05), Reading(501, 3)], 3);
        Require(recovered.Calls == 2 && recovered.Final.Bin == 3, "Entering the range must stop and use the final reading.");
        foreach (var nf in new[] { 1d, 450, 1000 })
            Require((await Run([Reading(nf)], 3)).Calls == 1, "In-range reading must not retry.");
        var bin0 = await Run([Reading(1001, 0)], 2);
        Require(bin0.Calls == 3 && (bool)Property(Invoke(null, "ClassifyE4981AMeasurement", bin0.Final)!, "IsNg")!,
            "BIN0 outside range must retry and retain NG routing on exhaustion.");
        var loss = await Run([Reading(0.05) with { LossFailureReason = "loss" }], 2);
        Require(loss.Calls == 3 && loss.Final.LossRejected, "Simultaneous range/loss failures must not stack budgets or erase loss failure.");
        var mixed = await Run([Reading(0.05), Reading(450, 11), Reading(0.05), Reading(450)], 2);
        Require(mixed.Calls == 3 && mixed.Motions == 2 && mixed.Final.CapacitanceNf == 0.05,
            "Alternating fault/range retries must exhaust one shared limit.");
        Require((await Run([Reading(450, 11)], 0)).Calls == 1 &&
                (await Run([Reading(450) with { LossFailureReason = "loss" }], 0)).Calls == 1,
            "Zero retries must disable faults and loss retries as well as range retries.");
        var communicationCalls = 0;
        var finalAfterCommunication = await RangeRetry(_ => ++communicationCalls switch
            {
                1 => Task.FromResult(Reading(0.05)),
                2 => Task.FromException<E4981AMeasurementResult>(new IOException("transient")),
                _ => Task.FromResult(Reading(0.05))
            }, Complete, 2, (_, _) => Task.CompletedTask);
        Require(communicationCalls == 3 && finalAfterCommunication.CapacitanceNf == 0.05,
            "Communication errors and range failures must share a single retry budget.");
        using var cancellation = new CancellationTokenSource();
        var canceledCalls = 0;
        try
        {
            await RangeRetry(_ => { canceledCalls++; return Task.FromResult(Reading(0.05)); }, Complete, 3,
                (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
            throw new Exception("Canceled range retry continued.");
        }
        catch (OperationCanceledException) { }
        Require(canceledCalls == 1, "Cancellation triggered another measurement.");
    }

    private static Task<E4981AMeasurementResult> RangeRetry(
        Func<CancellationToken, Task<E4981AMeasurementResult>> measure,
        Func<E4981AMeasurementResult, bool> complete, int retryCount,
        Func<int, CancellationToken, Task> beforeRetry, CancellationToken token = default) =>
        (Task<E4981AMeasurementResult>)typeof(HomePage).Assembly
            .GetType("ControlHub.Services.Devices.TestMeasurementRetry")!
            .GetMethod("ExecuteAsync")!.MakeGenericMethod(typeof(E4981AMeasurementResult))
            .Invoke(null, [measure, complete, retryCount, beforeRetry, token, null])!;
}
