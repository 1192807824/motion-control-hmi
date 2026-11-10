using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckFirstSetBreakVacuum(string directory)
    {
        Require(JsonSerializer.Deserialize<HomePageSettings>("{}")!.FirstSetBreakVacuumEnabled,
            "Old settings and recipes must preserve enabled break vacuum");
        var store = new HomePageSettingsStore(Path.Combine(directory, "first-set-break-vacuum.json"));
        var home = new HomePage();
        typeof(HomePage).GetField("_homeSettingsStore", PrivateInstance)!.SetValue(home, store);
        home.ApplyRecipeSettings(new HomePageSettings { SM7110LowerLimit = 1e9, SM7110MaximumTestSeconds = 1 });
        var toggle = (CheckBox)home.FindName("FirstSetBreakVacuumEnabledCheckBox");
        Require(toggle.IsChecked == true, "Default checkbox must be checked");
        toggle.IsChecked = false;
        Require(!store.Load().FirstSetBreakVacuumEnabled && !home.CaptureRecipeSettings().FirstSetBreakVacuumEnabled,
            "Unchecked configuration must auto-save and enter recipe snapshot");
        var offRecipe = ProductRecipeStore.Clone(home.CaptureRecipeSettings());
        toggle.IsChecked = true;
        var onRecipe = ProductRecipeStore.Clone(home.CaptureRecipeSettings());
        foreach (var settings in new[] { offRecipe, onRecipe, offRecipe })
        {
            home.ApplyRecipeSettings(settings);
            Require(toggle.IsChecked == settings.FirstSetBreakVacuumEnabled,
                "A/B/A recipe application must restore each break-vacuum switch");
        }

        var card = DispatchProxy.Create<IMotionCard, BreakVacuumCard>();
        var raw = (BreakVacuumCard)(object)card;
        raw.Inner.Open();
        var motion = new MotionControlPage();
        typeof(MotionControlPage).GetField("_motionCard", PrivateInstance)!.SetValue(motion, card);
        typeof(MotionControlPage).GetField("_motionOptions", PrivateInstance)!.SetValue(motion, new MotionCardOptions { SimulationMode = true });
        home.AttachMotionController(motion);
        var pulse = typeof(HomePage).GetMethod("PulseNozzleBreakVacuumAsync", PrivateInstance)!;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            // Huge configured delays make a skipped wait observable without relying on stopwatch upper bounds.
            ((TextBox)home.FindName("VacuumBreakPulseTextBox")).Text = "60000";
            ((TextBox)home.FindName("VacuumValveSwitchDelayTextBox")).Text = "60000";
            var zPositions = typeof(HomePage).GetMethod("ReadProductionZPositions", PrivateInstance)!.Invoke(home, null);
            typeof(HomePage).GetField("_productionZPositions", PrivateInstance)!.SetValue(home, zPositions);
            var offSnapshot = typeof(HomePage).GetMethod("ReadProductionZDwellTimes", PrivateInstance)!.Invoke(home, null);
            typeof(HomePage).GetField("_productionZDwellTimes", PrivateInstance)!.SetValue(home, offSnapshot);
            toggle.IsChecked = true; // A later UI edit must not change an already captured sequence.
            foreach (var nozzle in new[] { 1, 2 })
            {
                raw.Writes.Clear();
                var task = (Task)pulse.Invoke(home, [VisionCalibrationAxisSet.First, nozzle, CancellationToken.None])!;
                Require(task.IsCompletedSuccessfully,
                    "Disabled first-set release must complete synchronously, skipping both 60000ms delays");
                var expected = nozzle == 1 ? new[] { (15, true), (14, true) } : new[] { (17, true), (16, true) };
                Require(raw.Writes.SequenceEqual(expected),
                    "Disabled first-set release must only close suction/break valves and never turn break vacuum on");
            }
            typeof(HomePage).GetField("_productionZDwellTimes", PrivateInstance)!.SetValue(home, null);
            ((TextBox)home.FindName("VacuumBreakPulseTextBox")).Text = "30";
            ((TextBox)home.FindName("VacuumValveSwitchDelayTextBox")).Text = "20";
            foreach (var axisSet in new[] { VisionCalibrationAxisSet.First, VisionCalibrationAxisSet.Second })
            {
                // Keep the first-set switch off when exercising the independent second set.
                toggle.IsChecked = axisSet == VisionCalibrationAxisSet.First;
                foreach (var nozzle in new[] { 1, 2 })
                {
                    raw.Writes.Clear();
                    var clock = Stopwatch.StartNew();
                    var task = (Task)pulse.Invoke(home, [axisSet, nozzle, CancellationToken.None])!;
                    WaitForValveTask(task);
                    var breakBit = axisSet == VisionCalibrationAxisSet.First ? (nozzle == 1 ? 14 : 16) : (nozzle == 1 ? 18 : 20);
                    Require(raw.Writes.Any(write => write == (breakBit, false)) && raw.Writes.Last() == (breakBit, true),
                        "Enabled first set and unchanged second set must still pulse and close their own break valve");
                    Require(clock.ElapsedMilliseconds >= 45, "Enabled release must retain configured 20ms switch plus 30ms pulse");
                }
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            raw.Inner.Close();
        }
        var parameters = new ParameterSettingsPage();
        var scroll = (ScrollViewer)home.DetachParameterSettingsPanel();
        parameters.AttachSettingsContent(scroll);
        parameters.Measure(new Size(1280, 854));
        parameters.Arrange(new Rect(0, 0, 1280, 854));
        parameters.UpdateLayout();
        var content = (FrameworkElement)scroll.Content;
        var point = toggle.TransformToAncestor(content).Transform(new Point());
        scroll.ScrollToVerticalOffset(Math.Max(0, point.Y - 20));
        parameters.UpdateLayout();
        var visible = toggle.TransformToAncestor(parameters).Transform(new Point());
        Require(visible.X >= 0 && visible.X + toggle.ActualWidth <= 1280 && visible.Y >= 0 && visible.Y < 854,
            "Checkbox must fit the existing Z parameter card");
        var bitmap = new RenderTargetBitmap(1280, 854, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(parameters);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var previewDir = Path.Combine(Environment.CurrentDirectory, ".codex-tmp", "first-set-break-vacuum");
        Directory.CreateDirectory(previewDir);
        using (var output = File.Create(Path.Combine(previewDir, "parameters.png"))) encoder.Save(output);
        Console.WriteLine("PASS: first-set break-vacuum switch default/save/recipes, both nozzles close suction with no pulse or waits, captured flag, enabled timing, unchanged second set and existing-card layout. Simulated hardware only.");
    }

    private static void WaitForValveTask(Task task)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background,
            (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(task.IsCompleted, "Valve sequence did not complete before timeout");
        task.GetAwaiter().GetResult();
    }
}

public class BreakVacuumCard : DispatchProxy
{
    public SimulatedMotionCard Inner { get; } = new(new MotionCardOptions { SimulationMode = true });
    public List<(int Bit, bool High)> Writes { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IMotionCard.WriteDigitalOutput)) Writes.Add(((int)args![0]!, (bool)args[1]!));
        try { return method.Invoke(Inner, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException!; }
    }
}
