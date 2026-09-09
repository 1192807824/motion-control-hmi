using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Services.Persistence;
using ControlHub.Views.Controls;
using ControlHub.Views.Pages;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static HomePage _home = null!;

    [STAThread]
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "observation-compensation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _ = new Application();
        // Construct views only: never load the main window or attach a motion/camera controller.
        // HomePage reads saved values on construction; all subsequent saves use this temporary store.
        _home = new HomePage();
        var store = new HomePageSettingsStore(Path.Combine(directory, "home.json"));
        typeof(HomePage).GetField("_homeSettingsStore", Instance)!.SetValue(_home, store);
        _home.ApplyRecipeSettings(new HomePageSettings());
        Require(!Toggle().IsChecked!.Value && X().Text == "0" && Y().Text == "0" && R().Text == "0", "Old/default settings stay disabled with zero offsets");

        var normal = typeof(HomePage).GetMethod("CalculateLowerCameraPlacementTarget", Static)!.Invoke(null,
            [100000d, 200000d, 1000d, new VisionLowerCameraCorrectionResult(0.02, -0.03, 0), -1d])!;
        Near(Value(normal, "X"), 100200, "Vision X is converted from mm exactly once");
        Near(Value(normal, "Y"), 199700, "Vision Y is converted from mm exactly once");

        X().Text = "+200.5";
        Y().Text = "-300.25";
        R().Text = "-90";
        Toggle().IsChecked = true;
        var snapshot = Read();
        var target = Apply(normal, 1, true, snapshot);
        Near(Value(target, "X"), 100400.5, "Signed X is added as pulses");
        Near(Value(target, "Y"), 199399.75, "Signed Y is added as pulses");
        Near(Value(target, "R"), -31768, "Minus 90 degrees adds minus 32768 pulses after vision correction");
        Near(Value(target, "RCorrectionPulses"), -32768, "Motion status includes fixed rotation");
        Require(Equals(normal, Apply(normal, 2, true, snapshot)), "Nozzle2 is unchanged");
        Require(Equals(normal, Apply(normal, 1, false, snapshot)), "Failed or skipped vision correction is unchanged");

        foreach (var (degrees, expected) in new[] { (360d, 131072d), (90d, 32768d), (-180d, -65536d), (1d, 131072d / 360d), (-0.5d, -131072d / 720d) })
        {
            R().Text = degrees.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Near(Value(Apply(normal, 1, true, Read()), "R"), 1000 + expected, "R preserves sign, fractions and full turns");
        }
        Near(Value(Apply(normal, 1, true, snapshot), "R"), -31768, "Captured production snapshot is unaffected by later edits");

        var saved = store.Load();
        Require(saved.ObservationCompensationEnabled && saved.ObservationCompensationXPulses == 200.5 && saved.ObservationCompensationYPulses == -300.25 && saved.ObservationCompensationRDegrees == -0.5, "Signed values auto-save and reload");
        var recipe = new ProductRecipeStore(Path.Combine(directory, "recipes")).Create("观测补偿测试");
        recipe.Production = _home.CaptureRecipeSettings();
        var recipeStore = new ProductRecipeStore(Path.Combine(directory, "recipes"));
        recipeStore.Save(recipe);
        _home.ApplyRecipeSettings(new HomePageSettings());
        _home.ApplyRecipeSettings(recipeStore.LoadAll().Single().Production);
        Require(Toggle().IsChecked == true && X().Text == "200.5" && Y().Text == "-300.25" && R().Text == "-0.5", "Recipe save and switch restore enable state and all offsets");

        foreach (var invalid in new[] { "-", "", "NaN", "Infinity", "abc", "1e309" })
        {
            X().Text = invalid;
            Require(!(bool)Invoke("ObservationCompensationInputsValid")!, "Invalid active input blocks start");
            Throws(() => Read(), "Invalid active input cannot be captured for production");
            Throws(() => _home.CaptureRecipeSettings(), "Invalid draft cannot silently save old values into a recipe");
            Require(store.Load().ObservationCompensationXPulses == 200.5, "Invalid draft does not overwrite saved offsets");
        }
        X().Text = "200.5";
        R().Text = "1e308";
        Require(!(bool)Invoke("ObservationCompensationInputsValid")!, "R pulse overflow blocks start");
        Throws(() => Read(), "R pulse overflow cannot start motion");
        R().Text = "360";
        X().Text = "1e308";
        var targetType = normal.GetType();
        var huge = Activator.CreateInstance(targetType, [1e308, 0d, 0d, 0d])!;
        Throws(() => Apply(huge, 1, true, Read()), "Final target overflow is rejected");

        Toggle().IsChecked = false;
        Require(Equals(normal, Apply(normal, 1, true, Read())), "Disabled toggle leaves normal target unchanged");
        Require(!store.Load().ObservationCompensationEnabled, "Disabled state persists");
        X().Text = "-";
        Require((bool)Invoke("ObservationCompensationInputsValid")!, "Disabled compensation does not block production on unused draft");
        Invoke("UpdateObservationCompensationCommandState", false);
        Require(!Toggle().IsEnabled && !X().IsEnabled && !Y().IsEnabled && !R().IsEnabled, "All four controls lock during operation");
        Invoke("UpdateObservationCompensationCommandState", true);
        Require(Toggle().IsEnabled && X().IsEnabled && Y().IsEnabled && R().IsEnabled, "Controls unlock when idle");

        X().Text = "+200.5";
        Y().Text = "-300.25";
        R().Text = "-0.5";
        Toggle().IsChecked = true;
        var parameters = new ParameterSettingsPage();
        parameters.AttachSettingsContent(_home.DetachParameterSettingsPanel());
        parameters.Measure(new Size(1440, 854));
        parameters.Arrange(new Rect(0, 0, 1440, 854));
        parameters.UpdateLayout();
        var card = (Border)_home.FindName("ObservationCompensationCard");
        Require(card.Parent is Grid && card.ActualWidth > 500, "Compensation card is integrated into the parameter grid");
        ((Grid)card.Parent).Children.Remove(card);
        card.Margin = new Thickness(0);
        var preview = new Border { Child = card, Width = 630, Background = Brushes.Black };
        preview.Measure(new Size(630, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, 630, preview.DesiredSize.Height));
        preview.UpdateLayout();
        preview.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Require(X().ActualWidth > 150 && R().ActualHeight >= 30, "Signed inputs remain readable after parameter styling");
        var bitmap = new RenderTargetBitmap(630, (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(preview);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, "card.png"))) encoder.Save(stream);
        Console.WriteLine("PASS: signed XY pulses, R degree conversion, nozzle1-only success gate, disabled state, frozen snapshot, overflow/invalid input, auto-save, recipe round trip, control locks and parameter layout. No hardware commands issued.");
        Console.WriteLine("Artifacts: " + directory);
    }

    private static CheckBox Toggle() => (CheckBox)_home.FindName("ObservationCompensationEnabledCheckBox");
    private static TextBox X() => (TextBox)_home.FindName("ObservationCompensationXTextBox");
    private static TextBox Y() => (TextBox)_home.FindName("ObservationCompensationYTextBox");
    private static TextBox R() => (TextBox)_home.FindName("ObservationCompensationRTextBox");
    private static object? Invoke(string method, params object[] args) => typeof(HomePage).GetMethod(method, Instance)!.Invoke(_home, args);
    private static object Read() => Invoke("ReadObservationCompensationFromInputs")!;
    private static object Apply(object target, int nozzle, bool success, object compensation) => typeof(HomePage).GetMethod("ApplyObservationCompensation", Static)!.Invoke(null, [target, nozzle, success, compensation])!;
    private static double Value(object value, string name) => (double)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static void Near(double actual, double expected, string message) => Require(Math.Abs(actual - expected) < 1e-7, message);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException or ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }
}
