using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static void Main()
    {
        foreach (var status in new[] { 1, 2 })
        foreach (int? bin in new int?[] { null, 0, 1, 11 })
            CheckResult(new(status, 1e-10, 0.01, bin, ""), "BIN0", false);
        foreach (var bin in new[] { 0, 1, 2, 3, 4, 10, 11 })
            CheckResult(new(0, 1e-10, 0.01, bin, ""), bin is >= 1 and <= 3 ? $"BIN{bin}" : "BIN0", bin is >= 1 and <= 3);
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
        Require(JsonSerializer.Deserialize<HomePageSettings>("{}")!.TestStationDwellMilliseconds == 200,
            "Old settings must default to 200 ms.");
        var settingsPath = Path.Combine(Path.GetTempPath(), "test-station-dwell-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new HomePageSettingsStore(settingsPath);
            store.Save(new HomePageSettings { TestStationDwellMilliseconds = 350 });
            Require(store.Load().TestStationDwellMilliseconds == 350, "Custom dwell not persisted.");
            store.Save(new HomePageSettings { TestStationDwellMilliseconds = 0 });
            Require(store.Load().TestStationDwellMilliseconds == 0, "Zero dwell must be preserved.");
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
    }

    private static void CheckResult(E4981AMeasurementResult input, string bin, bool passed)
    {
        var result = Invoke(null, "ClassifyE4981AMeasurement", input)!;
        Require((string?)result.GetType().GetProperty("Bin")!.GetValue(result) == bin, "Wrong destination BIN.");
        Require((bool)result.GetType().GetProperty("Passed")!.GetValue(result)! == passed, "Wrong pass/fail result.");
    }

    private static object? Invoke(object? target, string name, params object?[] args) =>
        typeof(HomePage).GetMethod(name, Private)!.Invoke(target, args);

    private static void Set(object target, string name, object value) =>
        typeof(HomePage).GetField(name, Private)!.SetValue(target, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
