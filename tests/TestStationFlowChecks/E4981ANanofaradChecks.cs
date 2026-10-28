using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckE4981ANanofaradUnits()
    {
        const string legacyJson = """
            {"ComparatorEnabled":true,"Bin1LowerPf":-9999,"Bin1UpperPf":0,
             "Bin2LowerPf":0,"Bin2UpperPf":600000,"Bin3LowerPf":600000,"Bin3UpperPf":1200000}
            """;
        var settings = JsonSerializer.Deserialize<TcpConnectionSettings>(legacyJson)!;
        Require(settings.Bin1LowerNf == -9.999 && settings.Bin1UpperNf == 0 &&
                settings.Bin2LowerNf == 0 && settings.Bin2UpperNf == 600 &&
                settings.Bin3LowerNf == 600 && settings.Bin3UpperNf == 1200,
            "Legacy pF limits must display the same physical range in nF.");
        Require(E4981AProtocol.BuildSetupCommands(settings).Contains("CALC1:COMP:PRIM:BIN2 0,6E-07"),
            "Legacy 600000 pF upper limit must still send 600 nF in farads.");

        var viewModel = new MainWindowViewModel(new Dictionary<int, AxisSettings>());
        var page = new ConnectionConfigPage { DataContext = viewModel };
        var inputs = Descendants(page).OfType<TextBox>()
            .Where(input => BindingOperations.GetBinding(input, TextBox.TextProperty)?.Path.Path
                is string path && path.StartsWith("TcpConnectionSettings.Bin") && path.EndsWith("Nf"))
            .ToArray();
        Require(inputs.Length == 6, "All six capacitance limit inputs must bind to nF values.");
        foreach (var input in inputs)
        {
            // Inactive tabs have not entered a presentation source; supply their inherited context without loading hardware UI.
            input.DataContext = viewModel;
            input.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var binding = input.GetBindingExpression(TextBox.TextProperty)!;
            var name = binding.ParentBinding.Path.Path.Split('.').Last();
            var nfProperty = typeof(TcpConnectionSettings).GetProperty(name)!;
            var pfProperty = typeof(TcpConnectionSettings).GetProperty(name.Replace("Nf", "Pf"))!;
            pfProperty.SetValue(viewModel.TcpConnectionSettings, 600000d);
            binding.UpdateTarget();
            Require(double.Parse(input.Text) == 600, "Loaded legacy range must display in nF.");
            input.Text = "450";
            binding.UpdateSource();
            Require((double)nfProperty.GetValue(viewModel.TcpConnectionSettings)! == 450 &&
                    (double)pfProperty.GetValue(viewModel.TcpConnectionSettings)! == 450000,
                "Entering nF must persist the same physical capacitance through the legacy storage field.");
            var changed = new List<string?>();
            viewModel.TcpConnectionSettings.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            pfProperty.SetValue(viewModel.TcpConnectionSettings, 700000d);
            Require(changed.Contains(name), "Recipe updates must notify the displayed nF property.");
        }

        settings.Bin2UpperNf = 450;
        var restored = ProductRecipeStore.Clone(settings);
        Require(restored.Bin2UpperNf == 450 && restored.Bin2UpperPf == 450000,
            "Saving and reloading must not apply the unit conversion twice.");
        Require(!JsonSerializer.Serialize(settings).Contains("Nf"), "Persistence must have a single unambiguous unit.");
        var recipe = ProductRecipeStore.Clone(new ProductRecipe { E4981A = settings });
        Require(recipe.E4981A.Bin2UpperNf == 450 && recipe.E4981A.Bin3UpperNf == 1200,
            "Recipe round-trip must preserve nF input and unchanged legacy ranges.");
        Require(E4981AProtocol.BuildSetupCommands(restored).Contains("CALC1:COMP:PRIM:BIN2 0,4.5E-07"),
            "450 nF input must send 4.5E-7 F to the instrument.");

        var reading = E4981AProtocol.ParseMeasurement("0,4.08873E-7,0.126388,2");
        Require(Math.Abs(reading.CapacitanceNf - 408.873) < 1e-9, "Wire readings in F must convert to nF.");
        typeof(ConnectionConfigPage).GetMethod("UpdateMeterResult", Private)!.Invoke(page, [reading]);
        Require(((TextBlock)page.FindName("MeterCapacitanceText")).Text == "408.873 nF",
            "Connection test result must display nF.");
        var production = Invoke(null, "ClassifyE4981AMeasurement", reading)!;
        Require(((string)Property(production, "DisplayText")!).Contains("C=408.873nF"),
            "Production result must display nF.");
        Console.WriteLine("PASS: all six nF input bindings, legacy pF limits, settings/recipe round-trip, farad wire commands and nF measurement displays.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
