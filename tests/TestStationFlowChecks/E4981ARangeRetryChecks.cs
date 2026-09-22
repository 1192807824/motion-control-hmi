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
        var legacy = JsonSerializer.Deserialize<HomePageSettings>("{}")!;
        legacy.TestStationSettings = new()
        {
            [5] = new() { Enabled = true, Instrument = TestStationInstrument.E4981A },
            [6] = new() { Enabled = false, Instrument = TestStationInstrument.None },
            [7] = new() { Enabled = false, Instrument = TestStationInstrument.None }
        };
        home.ApplyRecipeSettings(legacy);
        var lower = (TextBox)home.FindName("E4981ARetryLowerTextBox");
        var upper = (TextBox)home.FindName("E4981ARetryUpperTextBox");
        var count = (TextBox)home.FindName("E4981ARangeRetryCountTextBox");
        Require(count.Text == "0" && lower.Text == "" && upper.Text == "",
            "Legacy recipes must leave range retries disabled without requiring limits.");
        lower.Text = "400";
        upper.Text = "500";
        count.Text = "3";
        var saved = store.Load();
        Require(saved.E4981ARetryLowerNf == 400 && saved.E4981ARetryUpperNf == 500 && saved.E4981ARangeRetryCount == 3,
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
            Require(store.Load().E4981ARangeRetryCount == 3, "Invalid count overwrote the last saved value.");
        }
        count.Text = "3";
        foreach (var invalid in new[] { "", "501", "NaN", "Infinity", "abc" })
        {
            lower.Text = invalid;
            ExpectFailure<ArgumentException>(() => Invoke(home, "ReadE4981ARetryRange"));
            try { home.CaptureRecipeSettings(); throw new Exception("Invalid range saved into recipe."); }
            catch (ArgumentException) { }
            Require(!(bool)Invoke(home, "AllTestStationParametersValid")!, "Invalid range must block production start.");
            Require(store.Load().E4981ARetryLowerNf == 400, "Invalid range overwrote the saved lower limit.");
        }
        home.ApplyRecipeSettings(legacy);
        home.ApplyRecipeSettings(new ProductRecipeStore(Path.Combine(directory, "e4981a-recipes"))
            .LoadAll().Single(r => r.Id == product.Id).Production);
        Require(lower.Text == "400" && upper.Text == "500" && count.Text == "3",
            "Recipe switch/reload must restore all range fields.");
        Set(home, "_startSequenceRunning", true);
        Invoke(home, "UpdateHomeCommandState");
        Require(!lower.IsEnabled && !upper.IsEnabled && !count.IsEnabled, "Production must lock the range editors.");
        Set(home, "_startSequenceRunning", false);
        Invoke(home, "UpdateHomeCommandState");
        upper.Text = "600";
        Require((double?)Property(snapshot, "UpperNf") == 500, "Editing mutated the production snapshot.");
        home.ApplyRecipeSettings(recipe);
        var parameters = new ParameterSettingsPage();
        parameters.AttachSettingsContent(home.DetachParameterSettingsPanel());
        Require(IsLogicalDescendant(lower, parameters), "Range editor must appear in parameter configuration.");
        RenderCard(lower, Path.Combine(directory, "e4981a-range-retry.png"));
        Require(lower.ActualWidth > 150 && lower.ActualHeight >= 30, "Range input is clipped or too small.");
        CheckE4981ARangeRetryFlowAsync(snapshot).GetAwaiter().GetResult();
        Console.WriteLine("PASS: E4981A range boundaries, retry budgets, final routing, cancellation, real parameter UI, validation, auto-save, recipe disk reload and frozen configuration.");
    }

    private static async Task CheckE4981ARangeRetryFlowAsync(object range)
    {
        bool Contains(E4981AMeasurementResult reading) =>
            (bool)range.GetType().GetMethod("Contains")!.Invoke(range, [reading])!;
        static E4981AMeasurementResult Reading(double nf, int bin = 2) => new(0, nf / 1e9, 0.01, bin, "");
        foreach (var nf in new[] { 400d, 450, 500 })
            Require(Contains(Reading(nf)), "Range must include both boundaries.");
        foreach (var nf in new[] { 399.999, 500.001, double.NaN, double.PositiveInfinity })
            Require(!Contains(Reading(nf)), "Outside/invalid reading triggered range retry.");
        Require(!Contains(Reading(450, 11)) && !Contains(Reading(450) with { Status = 2 }),
            "Instrument faults must use the original failure retry budget.");

        async Task<(E4981AMeasurementResult Final, int Calls, int Motions)> Run(
            E4981AMeasurementResult[] readings, int rangeCount, int failureCount = 0)
        {
            var calls = 0;
            var motions = 0;
            var final = await RangeRetry(_ => Task.FromResult(readings[Math.Min(calls++, readings.Length - 1)]),
                Contains, rangeCount, failureCount,
                (number, _) => { Require(number == ++motions, "Retry sequence skipped or duplicated a mechanical cycle."); return Task.CompletedTask; });
            return (final, calls, motions);
        }
        foreach (var limit in new[] { 0, 1, 3, 10 })
        {
            var result = await Run([Reading(450)], limit);
            Require(result.Calls == limit + 1 && result.Motions == limit, "Range retries did not exhaust exactly N extra measurements.");
            var classified = Invoke(null, "ClassifyE4981AMeasurement", result.Final)!;
            Require((string?)Property(classified, "Bin") == "BIN2" && !(bool)Property(classified, "IsNg")!,
                "Exhausted range retries must retain the last normal BIN classification.");
        }
        var escaped = await Run([Reading(450), Reading(501, 3)], 3);
        Require(escaped.Calls == 2 && escaped.Final.Bin == 3, "Leaving the range must stop and use the final reading.");
        Require((await Run([Reading(399)], 3)).Calls == 1, "Outside range must not retry a good reading.");
        var bin0 = await Run([Reading(450, 0)], 2);
        Require(bin0.Calls == 3 && (bool)Property(Invoke(null, "ClassifyE4981AMeasurement", bin0.Final)!, "IsNg")!,
            "BIN0 in range must retry and retain NG routing on exhaustion.");
        var loss = await Run([Reading(450) with { LossFailureReason = "loss" }], 2, 5);
        Require(loss.Calls == 3 && loss.Final.LossRejected, "Range exhaustion must not fall through into failure retries or erase loss failure.");
        var mixed = await Run([Reading(450), Reading(450, 11), Reading(450), Reading(450)], 2, 1);
        Require(mixed.Calls == 4 && mixed.Motions == 3, "Fault/range retry counts must remain independent and bounded.");
        Require((await Run([Reading(450, 11)], 3, 1)).Calls == 2, "Range budget must not extend persistent instrument faults.");
        using var cancellation = new CancellationTokenSource();
        var canceledCalls = 0;
        try
        {
            await RangeRetry(_ => { canceledCalls++; return Task.FromResult(Reading(450)); }, Contains, 3, 0,
                (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
            throw new Exception("Canceled range retry continued.");
        }
        catch (OperationCanceledException) { }
        Require(canceledCalls == 1, "Cancellation triggered another measurement.");
    }

    private static Task<E4981AMeasurementResult> RangeRetry(
        Func<CancellationToken, Task<E4981AMeasurementResult>> measure,
        Func<E4981AMeasurementResult, bool> contains, int rangeCount, int failureCount,
        Func<int, CancellationToken, Task> beforeRetry, CancellationToken token = default) =>
        (Task<E4981AMeasurementResult>)typeof(HomePage).Assembly
            .GetType("ControlHub.Services.Devices.TestMeasurementRetry")!
            .GetMethod("ExecuteWithRangeAsync")!.MakeGenericMethod(typeof(E4981AMeasurementResult))
            .Invoke(null, [measure, (Func<E4981AMeasurementResult, bool>)(reading => reading.IsSuccessful && !reading.LossRejected),
                failureCount, beforeRetry, token, contains, rangeCount, null])!;
}
