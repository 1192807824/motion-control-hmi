using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckAllParameterControls(HomePage home, HomePageSettings defaults, ProductRecipeStore store)
    {
        home.ApplyRecipeSettings(defaults);
        var fields = typeof(HomePage).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var inputs = fields.Where(field => field.FieldType == typeof(TextBox))
            // Upper limit is not used or saved in resistance mode; it is checked separately in current mode.
            // Batch numbers are free text and are not numeric recipe parameters.
            .Where(field => field.Name is not ("SM7110UpperLimitTextBox" or "BatchNumberTextBox"))
            .ToDictionary(field => field.Name, field => (TextBox)field.GetValue(home)!);
        var axisEditors = (IDictionary)typeof(HomePage).GetField("_productionAxisMotionEditors", PrivateInstance)!.GetValue(home)!;
        foreach (DictionaryEntry axis in axisEditors)
        {
            foreach (var property in axis.Value!.GetType().GetProperties().Where(property => property.PropertyType == typeof(TextBox)))
                inputs.Add($"Axis{axis.Key}.{property.Name}", (TextBox)property.GetValue(axis.Value)!);
        }
        Require(inputs.Count > 150, "All named and dynamically created numeric controls must be covered.");
        var sequence = 0;
        foreach (var (name, input) in inputs)
        {
            var value = name switch
            {
                "VisionPickupCountTextBox" => 30,
                "TestRetryCountTextBox" => 2,
                "E4981ARetryLowerTextBox" => 400.123456789,
                "E4981ARetryUpperTextBox" => 500.123456789,
                "VacuumPickupDwellTextBox" or "VacuumBreakPulseTextBox" or "VacuumValveSwitchDelayTextBox" or "TestStationDwellTextBox" => 123,
                "SM7110LowerLimitTextBox" => 2.5,
                "SM7110MaximumTimeTextBox" => 1.75,
                _ when name.Contains("PickupZPosition") => 30000.123456789,
                _ when name.Contains("DropZPosition") => 4800.234567891,
                _ when name.Contains("SafeZPosition") => -5000.345678912,
                _ when name.EndsWith(".RunVelocity") => 10000.456789123,
                _ => ++sequence + 0.123456789
            };
            input.Text = value.ToString("R", CultureInfo.CurrentCulture);
        }
        var toggles = fields.Where(field => typeof(ToggleButton).IsAssignableFrom(field.FieldType))
            .Select(field => (ToggleButton)field.GetValue(home)!).ToArray();
        foreach (var toggle in toggles) toggle.IsChecked = !(toggle.IsChecked == true);
        var instruments = fields.Where(field => field.FieldType == typeof(ComboBox) && field.Name.Contains("Instrument"))
            .Select(field => (ComboBox)field.GetValue(home)!).ToArray();
        foreach (var instrument in instruments) instrument.SelectedIndex = (instrument.SelectedIndex + 1) % instrument.Items.Count;
        var expectedInstruments = instruments.ToDictionary(input => input.Name, input => input.SelectedIndex);
        var expected = inputs.ToDictionary(pair => pair.Key, pair => double.Parse(pair.Value.Text, CultureInfo.CurrentCulture));
        var expectedToggles = toggles.ToDictionary(toggle => toggle.Name, toggle => toggle.IsChecked);
        var recipe = store.Create("全部参数精度测试");
        recipe.Production = home.CaptureRecipeSettings();
        store.Save(recipe);
        var before = store.Serialize(recipe);
        home.ApplyRecipeSettings(defaults);
        var failures = new List<string>();
        home.ApplyRecipeSettings(store.LoadAll().Single(saved => saved.Id == recipe.Id).Production);
        foreach (var (name, input) in inputs)
            if (double.Parse(input.Text, CultureInfo.CurrentCulture) != expected[name])
                failures.Add($"Precision/restore mismatch: {name}: {expected[name]:R} -> {input.Text}");
        foreach (var toggle in toggles)
            Require(toggle.IsChecked == expectedToggles[toggle.Name], $"Switch state lost: {toggle.Name}");
        foreach (var instrument in instruments)
            Require(instrument.SelectedIndex == expectedInstruments[instrument.Name], $"Instrument selection lost: {instrument.Name}");
        recipe.Production = home.CaptureRecipeSettings();
        if (store.Serialize(recipe) != before) failures.Add("Re-saving after recipe application changed production values.");

        foreach (var (name, input) in inputs)
        {
            var valid = input.Text;
            input.Text = "-";
            var rejected = false;
            try { home.CaptureRecipeSettings(); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { rejected = true; }
            if (!rejected) failures.Add($"Invalid draft silently accepted: {name}");
            input.Text = valid;
        }
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Console.WriteLine($"PASS: {inputs.Count} numeric controls preserve full precision through disk and recipe switching, reject invalid drafts; {toggles.Length} switches and {instruments.Length} instrument selections restore.");

        foreach (var (name, invalid) in new[]
                 {
                     ("VacuumPickupDwellTextBox", "60001"),
                     ("VacuumBreakPulseTextBox", "-1"),
                     ("FirstSetNozzle2PreDropTextBox", "999999"),
                     ("FirstSetNozzle2PlacePreDropTextBox", "999999"),
                     ("SecondSetNozzle1PreDropTextBox", "999999"),
                     ("TestRetryCountTextBox", "11"),
                     ("E4981ARetryLowerTextBox", "501"),
                     ("Axis0.RunVelocity", "0"),
                     ("Axis0.StartVelocity", "-1")
                 })
        {
            var input = inputs[name];
            var valid = input.Text;
            input.Text = invalid;
            var rejected = false;
            try { home.CaptureRecipeSettings(); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { rejected = true; }
            Require(rejected, $"Out-of-range draft must not silently save: {name} = {invalid}");
            input.Text = valid;
        }
        Console.WriteLine("PASS: out-of-range vacuum times, pre-drop distances, retry count and axis speeds reject recipe saving.");

        ((ComboBox)home.FindName("SM7110LimitModeComboBox")).SelectedValue = "A";
        ((TextBox)home.FindName("SM7110LowerLimitTextBox")).Text = "0.000001";
        ((TextBox)home.FindName("SM7110UpperLimitTextBox")).Text = "0.000002";
        var currentMode = home.CaptureRecipeSettings();
        home.ApplyRecipeSettings(defaults);
        home.ApplyRecipeSettings(currentMode);
        Require(((ComboBox)home.FindName("SM7110LimitModeComboBox")).SelectedValue as string == "A" &&
                double.Parse(((TextBox)home.FindName("SM7110UpperLimitTextBox")).Text, CultureInfo.CurrentCulture) == 0.000002,
                "Current mode and upper limit must restore.");
        Console.WriteLine("PASS: SM7110 current mode and upper/lower limits restore.");
        ((TextBox)home.FindName("Bin0PositionXTextBox")).Text = "";
        var cleared = home.CaptureRecipeSettings();
        Require(cleared.Bin0PositionX is null, "Clearing an optional position must clear its saved value.");
        home.ApplyRecipeSettings(defaults);
        home.ApplyRecipeSettings(cleared);
        Require(((TextBox)home.FindName("Bin0PositionXTextBox")).Text == "", "Optional blank positions must remain blank after switching.");
        CheckSaveCurrentButton(home, store, recipe);
    }

    private static void CheckSaveCurrentButton(HomePage home, ProductRecipeStore store, ProductRecipe recipe)
    {
        var parameters = new ParameterSettingsPage();
        // Keep all views detached: no Loaded event, native window, device connection or motion.
        var motion = new MotionControlPage();
        var viewModel = new ControlHub.ViewModels.MainWindowViewModel(new Dictionary<int, AxisSettings>());
        var vision = (VisualCalibrationPage)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VisualCalibrationPage));
        var visionHost = new ControlHub.Views.Controls.VisionMasterProcessHost();
        typeof(VisualCalibrationPage).GetField("VisionHost", PrivateInstance)!.SetValue(vision, visionHost);
        typeof(ParameterSettingsPage).GetField("_homePage", PrivateInstance)!.SetValue(parameters, home);
        typeof(ParameterSettingsPage).GetField("_recipeStore", PrivateInstance)!.SetValue(parameters, store);
        typeof(ParameterSettingsPage).GetField("_motionPage", PrivateInstance)!.SetValue(parameters, motion);
        typeof(ParameterSettingsPage).GetField("_visualCalibrationPage", PrivateInstance)!.SetValue(parameters, vision);
        typeof(ParameterSettingsPage).GetField("_viewModel", PrivateInstance)!.SetValue(parameters, viewModel);
        typeof(ParameterSettingsPage).GetMethod("RefreshRecipeList", PrivateInstance)!.Invoke(parameters, [recipe.Id]);
        var edits = new Dictionary<string, string>
        {
            ["InspectionProcedureNameTextBox"] = "产品A找料",
            ["NozzleTeachingProcedureNameTextBox"] = "产品A示教",
            ["CalibrationProcedureNameTextBox"] = "产品A标定",
            ["LowerCalibrationProcedureNameTextBox"] = "产品A下标定",
            ["RotationPointProcedureNameTextBox"] = "产品A三点",
            ["RotationCenterProcedureNameTextBox"] = "产品A旋转中心",
            ["LowerCorrectionProcedureNameTextBox"] = "产品A纠偏"
        };
        foreach (var (name, value) in edits) ((TextBox)parameters.FindName(name)).Text = value;
        var expectedProduction = ProductRecipeStore.Clone(home.CaptureRecipeSettings());
        typeof(ParameterSettingsPage).GetMethod("SaveCurrentRecipe_Click", PrivateInstance)!
            .Invoke(parameters, [parameters, new System.Windows.RoutedEventArgs()]);
        Require(((TextBlock)parameters.FindName("RecipeStatusText")).Text.Contains("已保存到"), "Save Current must report success for all valid inputs.");
        foreach (var (name, value) in edits)
            Require(((TextBox)parameters.FindName(name)).Text == value, "Save Current must retain edited vision names after refreshing the list.");
        var saved = store.LoadAll().Single(item => item.Id == recipe.Id);
        Require(System.Text.Json.JsonSerializer.Serialize(saved.Production) == System.Text.Json.JsonSerializer.Serialize(expectedProduction),
            "Save Current must persist the complete production snapshot.");
        Require(saved.VisionProcedureNames.Inspection == edits["InspectionProcedureNameTextBox"] &&
                visionHost.ProcedureNames.Inspection != saved.VisionProcedureNames.Inspection,
            "Saved vision names must come from the recipe editor, independently of the active runtime names.");
        var before = store.Serialize(saved);
        ((TextBox)parameters.FindName("InspectionProcedureNameTextBox")).Text = "";
        typeof(ParameterSettingsPage).GetMethod("SaveCurrentRecipe_Click", PrivateInstance)!
            .Invoke(parameters, [parameters, new System.Windows.RoutedEventArgs()]);
        Require(!((TextBlock)parameters.FindName("RecipeStatusText")).Text.Contains("已保存到") &&
                store.Serialize(store.LoadAll().Single(item => item.Id == recipe.Id)) == before,
            "Invalid vision names must reject Save Current without overwriting the recipe.");
        Console.WriteLine("PASS: actual Save Current button saves all production values and seven edited vision names; invalid names preserve the previous file.");
    }
}
