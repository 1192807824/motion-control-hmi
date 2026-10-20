using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ControlHub.Services.Motion;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        _ = new Application();
        CheckController();
        CheckPageIntegration();
        Console.WriteLine("PASS: state colors, active-high outputs, alarm/mute/rearm, real reset button under busy/safety-lock conditions, emergency ordering, partial write retries and process IO isolation. Simulated hardware only.");
    }

    private static void CheckController()
    {
        var card = DispatchProxy.Create<IMotionCard, RecordingCard>();
        var raw = (RecordingCard)(object)card;
        raw.Inner.Open();
        var signals = new MachineSignalController();
        signals.Apply(card);
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        var count = raw.Calls.Count;
        signals.Apply(card);
        Require(raw.Calls.Count == count, "Stable states must not write outputs on every 5ms poll");
        signals.SetOperatingState(MachineOperatingState.Running);
        signals.Apply(card);
        CheckOutputs(raw, red: false, green: true, yellow: false, buzzer: false);
        foreach (var state in new[] { MachineOperatingState.Standby, MachineOperatingState.Paused,
                     MachineOperatingState.Resetting, MachineOperatingState.Stopping })
        {
            signals.SetOperatingState(state);
            signals.Apply(card);
            CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        }

        signals.UpdateActiveFaults(["axis:1"]);
        signals.ReportAlarm("AXIS-1");
        signals.Apply(card);
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: true);
        signals.SilenceBuzzer();
        signals.Apply(card);
        signals.ResetLatchedAlarms();
        signals.UpdateActiveFaults(["axis:1"]);
        signals.Apply(card);
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        signals.UpdateActiveFaults(["axis:1", "axis:2"]);
        signals.Apply(card);
        Require(raw.High(0), "A different new fault must rearm the buzzer");
        signals.SilenceBuzzer();
        signals.UpdateActiveFaults([]);
        signals.UpdateActiveFaults(["axis:1"]);
        signals.Apply(card);
        Require(raw.High(0), "A resolved fault recurring must rearm the buzzer");
        signals.UpdateActiveFaults([]);
        signals.SilenceBuzzer();
        signals.Apply(card);
        raw.Inner.WriteDigitalOutput(0, true);
        signals.SilenceBuzzer();
        signals.Apply(card);
        Require(!raw.High(0), "Silence must close a buzzer manually opened through the IO page");

        signals.ReportAlarm("WRITE-FAILURE-TEST");
        raw.FailBit = 29;
        try { signals.Apply(card); throw new Exception("Output failure was hidden"); }
        catch (AggregateException) { }
        Require(raw.High(0), "A failed red output must not prevent the independent buzzer write");
        raw.FailBit = null;
        signals.Apply(card);
        Require(raw.High(29), "Failed output must retry rather than be cached as successful");
        signals.Shutdown();
        signals.Apply(card);
        CheckOutputs(raw, red: false, green: false, yellow: false, buzzer: false);
        Require(raw.Writes.All(write => write.Bit is 0 or 29 or 30 or 31), "Signal controller must never write process IO");
    }

    private static void CheckPageIntegration()
    {
        var card = DispatchProxy.Create<IMotionCard, RecordingCard>();
        var raw = (RecordingCard)(object)card;
        raw.Inner.Open();
        for (var bit = 1; bit < 29; bit++) raw.Inner.WriteDigitalOutput(bit, bit % 2 == 0);
        var processBefore = raw.Inner.ReadDigitalOutputs(0) & 0x1ffffffe;
        var motion = new MotionControlPage(); // Not loaded/shown: no native hardware connection.
        Set(motion, "_motionCard", card);
        var viewModel = new MainWindowViewModel();
        viewModel.PopulateMotionAxes(1);
        foreach (var axis in viewModel.Axes) { axis.IsAvailable = true; axis.StatusReadHealthy = true; }
        motion.DataContext = viewModel;
        var home = new HomePage();
        home.AttachMotionController(motion);
        Pump();
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        Set(home, "_startSequenceRunning", true);
        Call(home, "UpdateHomeCommandState");
        Pump();
        CheckOutputs(raw, red: false, green: true, yellow: false, buzzer: false);
        Set(home, "_productionPauseRequested", true);
        Call(home, "UpdateHomeCommandState");
        Pump();
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        Set(home, "_productionPauseRequested", false);
        Call(home, "UpdateHomeCommandState");
        Pump();
        raw.Inner.WriteDigitalOutput(0, true);
        Set(motion, "_calibrationOperationActive", true);
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        CheckOutputs(raw, red: false, green: true, yellow: false, buzzer: false);
        Set(motion, "_calibrationOperationActive", false);
        Set(home, "_startSequenceRunning", false);
        Call(home, "UpdateHomeCommandState");
        Pump();

        var before = raw.Writes.Count;
        motion.ReportMachineSignalAlarm("TEST-ALARM");
        Require(raw.Writes.Count == before, "Alarm recording must not perform IO before the safety stop");
        Pump();
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: true);
        Set(motion, "_motionSafetyLock", true);
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        Require((bool)Get(motion, "_motionSafetyLock")!, "Silence must not release the motion safety lock");
        motion.ReportMachineSignalAlarm("NEW-FAULT");
        Pump();
        Require(raw.High(0), "A new alarm after mute must sound again");
        Set(motion, "_calibrationOperationActive", true);
        var clears = raw.ClearCount;
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        Require(!raw.High(0) && raw.ClearCount == clears, "Busy reset must mute without clearing active motion alarms");
        Set(motion, "_calibrationOperationActive", false);
        Set(motion, "_motionSafetyLock", false);

        raw.AxisAlarm = true;
        Call(motion, "PollMotionState");
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        raw.AxisAlarm = false;
        Call(motion, "PollMotionState");
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        raw.AxisAlarm = true;
        Call(motion, "PollMotionState");
        Pump();
        Require(raw.High(0), "A hardware fault recurring after recovery must sound again");

        Set(home, "_startSequenceRunning", true);
        Call(home, "UpdateHomeCommandState");
        Pump();
        raw.Calls.Clear();
        Call(motion, "IssueEmergencyStopAndNotify");
        Require(raw.Calls.SequenceEqual(new[] { "EmergencyStop" }), "Hardware emergency stop must precede indicator IO");
        Require((bool)Get(home, "_preserveIoOnEmergencyStop")!, "Emergency notification must still freeze process IO");
        Pump();
        Require(raw.High(29) && raw.High(0), "Emergency stop must indicate red with buzzer");
        Require((raw.Inner.ReadDigitalOutputs(0) & 0x1ffffffe) == processBefore,
            "Startup/state changes, reset/mute and emergency signaling must preserve every process output");
    }

    private static void CheckOutputs(RecordingCard raw, bool red, bool green, bool yellow, bool buzzer) =>
        Require(raw.High(29) == red && raw.High(30) == green && raw.High(31) == yellow && raw.High(0) == buzzer,
            $"Wrong signals: expected red={red}, green={green}, yellow={yellow}, buzzer={buzzer}; actual port={raw.Inner.ReadDigitalOutputs(0):X8}");
    private static object? Get(object target, string field) => target.GetType().GetField(field, Private)!.GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

public class RecordingCard : DispatchProxy
{
    public SimulatedMotionCard Inner { get; } = new(new MotionCardOptions { SimulationMode = true });
    public List<string> Calls { get; } = [];
    public List<(int Bit, bool High)> Writes { get; } = [];
    public int? FailBit { get; set; }
    public bool AxisAlarm { get; set; }
    public int ClearCount { get; private set; }
    public bool High(int bit) => (Inner.ReadDigitalOutputs(0) & (1u << bit)) != 0;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IMotionCard.WriteDigitalOutput))
        {
            var bit = (int)args![0]!;
            var high = (bool)args[1]!;
            Calls.Add($"Y{bit:00}={high}");
            Writes.Add((bit, high));
            if (bit == FailBit) throw new MotionCardException("Injected signal output failure");
        }
        if (method.Name == nameof(IMotionCard.EmergencyStop)) Calls.Add("EmergencyStop");
        if (method.Name == nameof(IMotionCard.ClearAlarms)) ClearCount++;
        if (method.Name == nameof(IMotionCard.ReadAxis) && AxisAlarm)
            return Inner.ReadAxis((int)args![0]!) with { Alarm = true, AxisErrorCode = 42 };
        try { return method.Invoke(Inner, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException!; }
    }
}
