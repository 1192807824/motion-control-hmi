using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Services.Motion;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckDoorInterlock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "door-interlock-" + Guid.NewGuid().ToString("N"));
        var store = new MotionCardOptionsStore(Path.Combine(directory, "motion-settings.json"));
        Require(!store.Load().DoorInterlockEnabled, "Missing setting must default to disabled");
        var card = DispatchProxy.Create<IMotionCard, DoorInputCard>();
        var raw = (DoorInputCard)(object)card;
        raw.Inner.Open();
        var options = new MotionCardOptions { ExternalEmergencyStopEnabled = false };
        var motion = new MotionControlPage();
        Set(motion, "_motionOptions", options);
        Set(motion, "_motionOptionsStore", store);
        Set(motion, "_motionCard", card);
        var viewModel = new MainWindowViewModel();
        viewModel.PopulateMotionAxes(1);
        motion.DataContext = viewModel;
        var home = new HomePage();
        home.AttachMotionController(motion);
        motion.EmergencyStopIssued += (_, _) => raw.Events.Enqueue("freeze");
        Pump();
        for (var bit = 1; bit < 29; bit++) raw.Inner.WriteDigitalOutput(bit, bit % 2 == 0);
        var processBefore = raw.Inner.ReadDigitalOutputs(0) & 0x1ffffffe;
        try
        {
            raw.Inputs = 1u << 6;
            Call(motion, "RefreshDoorInterlock");
            Call(motion, "ThrowIfExternalEmergencyStopActive");
            Require(raw.EmergencyCount == 0 && raw.InputReadCount == 0,
                "Disabled door interlock must ignore even an open door without reading its input");

            var parameters = new ParameterSettingsPage();
            Set(parameters, "_motionPage", motion);
            Call(parameters, "RefreshDoorInterlockSetting");
            var toggle = (CheckBox)parameters.FindName("DoorInterlockEnabledCheckBox");
            Require(toggle.IsEnabled && toggle.IsChecked == false, "Parameter switch must show saved disabled state");
            toggle.IsChecked = true;
            Require(motion.DoorInterlockEnabled && store.Load().DoorInterlockEnabled && raw.EmergencyCount == 1,
                "Enabling with an open door must save and stop immediately, even with external E-stop monitoring disabled");
            Require((bool)Get(home, "_preserveIoOnEmergencyStop")!, "Door stop must freeze production IO");
            Require(raw.Events.Take(2).SequenceEqual(new[] { "freeze", "stop" }),
                "Production IO must freeze before the hardware emergency stop");
            ExpectDoorBlocked(motion);
            try { motion.StartExternalJog(0, 100, "door-test"); throw new Exception("Open door allowed a public jog command"); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("门禁")) { }
            Require(!motion.SetDigitalOutputHardwareBit(2, false) && raw.MoveCount == 0,
                "Public motion and process output APIs must reject an open door");
            Require(!(bool)typeof(MotionControlPage).GetMethod("EnsureProcessIoWriteReady", Private)!.Invoke(motion, null)!,
                "Open door must block process IO");
            Pump();
            CheckDoorLamp(raw, redOn: true);
            Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
            Pump();
            Require((raw.Inner.ReadDigitalOutputs(0) & 0xe0000001u) == 0x60000001u,
                "Alarm reset must acknowledge yellow even with the door held open");
            ExpectDoorBlocked(motion);
            Require(raw.MoveCount == 0, "Acknowledging the lamp must never bypass the door motion guard");
            Call(motion, "RefreshDoorInterlock");
            Require(raw.EmergencyCount == 1, "A held-open door must not repeatedly issue stops");

            raw.Inputs = 0;
            Require(SpinWait.SpinUntil(() => !(bool)Get(motion, "_doorInterlockInputActive")!, 2000),
                "Background monitor must observe door closure");
            Pump();
            Call(motion, "ThrowIfExternalEmergencyStopActive");
            Require(raw.MoveCount == 0 && raw.EmergencyCount == 1,
                "Closing the door must not restart any motion");

            // Deliberately do not pump the UI: hardware stopping must not depend on it.
            raw.Inputs = 1u << 6;
            Require(SpinWait.SpinUntil(() => raw.EmergencyCount == 2, 2000),
                "Door opening must stop from the independent thread while the UI is idle");
            Pump();
            Require((raw.Inner.ReadDigitalOutputs(0) & 0x1ffffffe) == processBefore,
                "Door emergency stop must preserve every process output");

            toggle.IsChecked = false;
            Require(!store.Load().DoorInterlockEnabled, "Disabling must persist across restart");
            Call(motion, "ThrowIfExternalEmergencyStopActive");
            raw.FailInputRead = true;
            Call(motion, "RefreshDoorInterlock");
            Require(raw.EmergencyCount == 2, "Disabled door must ignore failed input reads too");
            toggle.IsChecked = true;
            Require(raw.EmergencyCount == 3, "Enabled door input read failure must stop immediately");
            ExpectDoorBlocked(motion);
            raw.FailInputRead = false;
            raw.Inputs = 0;
            Call(motion, "RefreshDoorInterlock");
            Pump();
            Call(motion, "ThrowIfExternalEmergencyStopActive");
            Set(motion, "_motionSafetyLock", true);
            toggle.IsChecked = false;
            Require((bool)Get(motion, "_motionSafetyLock")!, "Disabling door must preserve other safety locks");

            // Recipe application must never change the machine-level switch.
            motion.SaveDoorInterlockEnabled(true);
            Require(raw.EmergencyCount == 3, "Enabling with all doors closed must not issue an emergency stop");
            motion.ApplyRecipeMotionSettings(motion.CaptureRecipeMotionSettings());
            Require(motion.DoorInterlockEnabled && store.Load().DoorInterlockEnabled,
                "Product recipe changes must preserve door interlock setting");

            Call(parameters, "RefreshDoorInterlockSetting");
            RenderDoorSetting(parameters);
            CheckDoorSaveFailure(directory);
        }
        finally
        {
            Call(motion, "StopExternalEmergencyStopMonitor");
            Set(motion, "_closed", true);
            Pump();
            card.Close();
        }
        CheckExternalStopWithDoorDisabled();
        Console.WriteLine("PASS: X06 active-high door, persisted parameter switch, already-open enable, independent stop without UI, re-open, input failure, disabled bypass, motion/IO gates, vacuum preservation, no auto restart, recipe isolation and external emergency stop independence. Simulated hardware only.");
    }

    private static void ExpectDoorBlocked(MotionControlPage motion)
    {
        try { Call(motion, "ThrowIfExternalEmergencyStopActive"); throw new Exception("Open door allowed motion"); }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException error && error.Message.Contains("门禁")) { }
    }

    private static void CheckDoorLamp(DoorInputCard raw, bool redOn) =>
        Require(((raw.Inner.ReadDigitalOutputs(0) & (1u << 29)) == 0) == redOn,
            "Open door must show active-low red alarm lamp");

    private static void CheckDoorSaveFailure(string directory)
    {
        var blocker = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blocker, "block directory creation");
        var motion = new MotionControlPage();
        Set(motion, "_motionOptions", new MotionCardOptions { DoorInterlockEnabled = true });
        Set(motion, "_motionOptionsStore", new MotionCardOptionsStore(Path.Combine(blocker, "motion-settings.json")));
        var parameters = new ParameterSettingsPage();
        Set(parameters, "_motionPage", motion);
        Call(parameters, "RefreshDoorInterlockSetting");
        var toggle = (CheckBox)parameters.FindName("DoorInterlockEnabledCheckBox");
        toggle.IsChecked = false;
        Require(toggle.IsChecked == true && motion.DoorInterlockEnabled,
            "Failed save must keep enabled setting and restore switch display");
    }

    private static void CheckExternalStopWithDoorDisabled()
    {
        var card = DispatchProxy.Create<IMotionCard, DoorInputCard>();
        var raw = (DoorInputCard)(object)card;
        raw.Inner.Open();
        var motion = new MotionControlPage();
        Set(motion, "_motionOptions", new MotionCardOptions { ExternalEmergencyStopEnabled = true, DoorInterlockEnabled = false });
        Set(motion, "_motionCard", card);
        try
        {
            // X01=0 is the existing active-low external emergency-stop input.
            Call(motion, "StartExternalEmergencyStopMonitor");
            Require(SpinWait.SpinUntil(() => raw.EmergencyCount > 0, 2000),
                "External emergency stop must keep working with the door switch disabled");
        }
        finally
        {
            Call(motion, "StopExternalEmergencyStopMonitor");
            Set(motion, "_closed", true);
            Pump();
            card.Close();
        }
    }

    private static void RenderDoorSetting(ParameterSettingsPage parameters)
    {
        foreach (var width in new[] { 1680, 1280 })
        {
            parameters.Measure(new Size(width, 854));
            parameters.Arrange(new Rect(0, 0, width, 854));
            parameters.UpdateLayout();
            var toggle = (CheckBox)parameters.FindName("DoorInterlockEnabledCheckBox");
            var point = toggle.TransformToAncestor(parameters).Transform(new Point());
            Require(point.X >= 0 && point.X + toggle.ActualWidth <= width && toggle.ActualHeight >= 16,
                "Door switch must be visible on parameter page");
            var bitmap = new RenderTargetBitmap(width, 854, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(parameters);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var previewDir = Path.Combine(Environment.CurrentDirectory, ".codex-tmp", "door-interlock");
            Directory.CreateDirectory(previewDir);
            using var output = File.Create(Path.Combine(previewDir, $"parameters-{width}.png"));
            encoder.Save(output);
        }
    }
}

public class DoorInputCard : DispatchProxy
{
    public SimulatedMotionCard Inner { get; } = new(new MotionCardOptions { SimulationMode = true });
    public volatile uint Inputs;
    public volatile bool FailInputRead;
    public ConcurrentQueue<string> Events { get; } = new();
    private int _emergencyCount;
    private int _inputReadCount;
    private int _moveCount;
    public int EmergencyCount => Volatile.Read(ref _emergencyCount);
    public int InputReadCount => Volatile.Read(ref _inputReadCount);
    public int MoveCount => Volatile.Read(ref _moveCount);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IMotionCard.ReadDigitalInputs))
        {
            Interlocked.Increment(ref _inputReadCount);
            if (FailInputRead) throw new MotionCardException("Injected door input read failure");
            if ((int)args![0]! != 0) throw new Exception("Door input must read module 1001 port 0");
            return Inputs;
        }
        if (method.Name == nameof(IMotionCard.EmergencyStop))
        {
            Events.Enqueue("stop");
            Inner.EmergencyStop();
            Interlocked.Increment(ref _emergencyCount);
            return null;
        }
        if (method.Name.StartsWith("Move", StringComparison.Ordinal)) Interlocked.Increment(ref _moveCount);
        try { return method.Invoke(Inner, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException!; }
    }
}
