using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckSM7110GigohmUnits()
    {
        var home = new HomePage();
        var connection = new ConnectionConfigPage();
        home.AttachConnectionConfigController(connection);
        var store = new HomePageSettingsStore(Path.Combine(AppContext.BaseDirectory, "ui-checks", "gigohm-home.json"));
        Set(home, "_homeSettingsStore", store);
        var legacy = JsonSerializer.Deserialize<HomePageSettings>("""
            {"SM7110LowerLimit":50000000000,"SM7110UpperLimit":100000000000,"SM7110LimitMeasurementMode":"R"}
            """)!;
        home.ApplyRecipeSettings(legacy);
        var lower = (TextBox)home.FindName("SM7110LowerLimitTextBox");
        var upper = (TextBox)home.FindName("SM7110UpperLimitTextBox");
        Require(lower.Text == "50" && upper.Text == "100", "Old ohm settings must load as gigohms.");
        var maximumTime = (TextBox)home.FindName("SM7110MaximumTimeTextBox");
        Require(maximumTime.Text == "", "Legacy recipes must require an explicit time limit.");
        ExpectFailure<ArgumentException>(() => Invoke(home, "ReadSM7110AcceptanceRange"));
        maximumTime.Text = "3";
        Require(((System.Windows.FrameworkElement)home.FindName("SM7110UpperLimitPanel")).Visibility == System.Windows.Visibility.Collapsed &&
                ((System.Windows.FrameworkElement)home.FindName("SM7110MaximumTimePanel")).Visibility == System.Windows.Visibility.Visible,
            "Resistance mode must show threshold/time and hide the former upper limit.");
        RenderCard(lower, Path.Combine(AppContext.BaseDirectory, "ui-checks", "sm7110-threshold.png"));
        var range = Invoke(home, "ReadSM7110AcceptanceRange")!;
        foreach (var value in new[] { 5e10 - 1, 5e10, 6.06344e10, 1e11, 1e11 + 1 })
        {
            var result = Invoke(null, "ClassifySM7110Measurement", new SM7110MeasurementResult(value, 0, "R", ""), "BIN2", range)!;
            Require((bool)Property(result, "Passed")! == (value >= 5e10),
                "Resistance must pass at/above the threshold regardless of the former upper bound.");
        }
        var wire = SM7110Protocol.ParseMeasurementResult("0,6.06344E+10", "R");
        typeof(ConnectionConfigPage).GetMethod("UpdateSerialMeterResult", Private)!.Invoke(connection, [wire]);
        Require(((TextBlock)connection.FindName("SerialMeterValueText")).Text == "60.6344" &&
                ((TextBlock)connection.FindName("SerialMeterUnitText")).Text == "GΩ",
            "Connection result must display 60.6344 GΩ.");
        Require(((TextBlock)connection.FindName("SerialMeterRawResultText")).Text == wire.RawResponse,
            "Raw wire data must remain available for diagnostics.");
        var production = Invoke(null, "ClassifySM7110Measurement", wire, "BIN2", range)!;
        var display = (string)Property(production, "DisplayText")!;
        Require(display.Contains("60.6344GΩ") && display.Contains("达标门限≥50GΩ"),
            "Production value and acceptance range must use the same display unit.");

        lower.Text = "60";
        upper.Text = "80";
        Require(store.Load().SM7110LowerLimit == 6e10 && store.Load().SM7110UpperLimit == 1e11 && store.Load().SM7110MaximumTestSeconds == 3,
            "Save the new threshold/time while preserving unused legacy upper bounds.");
        var saved = ProductRecipeStore.Clone(store.Load());
        home.ApplyRecipeSettings(saved);
        Require(lower.Text == "60" && maximumTime.Text == "3", "Recipe reload must preserve threshold and time.");
        lower.Text = "1e308";
        ExpectFailure<ArgumentException>(() => Invoke(home, "ReadSM7110AcceptanceRange"));
        Require(store.Load().SM7110LowerLimit == 6e10, "Overflowing unit conversion must not overwrite valid limits.");
        home.ApplyRecipeSettings(saved);
        lower.Text = "";
        Require(store.Load().SM7110LowerLimit is null, "An empty threshold must remain unset.");

        home.ApplyRecipeSettings(new HomePageSettings
        {
            SM7110LowerLimit = -1e-12, SM7110UpperLimit = 2e-9, SM7110LimitMeasurementMode = "A"
        });
        Require(double.Parse(lower.Text) == -1e-12 && double.Parse(upper.Text) == 2e-9,
            "Current-mode limits must retain their ampere units.");
        foreach (var mode in new[] { "A", "RS", "RV", "RL" })
        {
            var result = new SM7110MeasurementResult(123, 0, mode, "");
            Require(result.DisplayValue == result.Value && result.DisplayUnit == result.Unit,
                "Changing resistance display must preserve other measurement modes.");
        }
        Console.WriteLine("PASS: GΩ inputs/results, legacy Ω settings, recipe reload, inclusive routing, overflow/empty limits and other modes.");
    }
}
