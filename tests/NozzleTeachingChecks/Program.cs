using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.Views.Controls;
using ControlHub.Views.Pages;
using VisionMasterHost;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--fake-host-process"))
        {
            Console.ReadLine();
            return;
        }
        CheckCircleGeometry();
        CheckReplacementVerification();
        CheckAlgorithmFailureRecovery();
        CheckRunningHostCapability();
        var directory = Path.Combine(Path.GetTempPath(), "nozzle-teaching-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CheckIndependentResults(directory);
        Console.WriteLine("PASS: live host capability handshake (current/legacy/mismatched), three-point circle, invalid points, algorithm NG/exception recovery, missing-current-image rejection, Z1/Z2 independent drafts and offsets, replacement verification, event validation, partial profile save/load, persisted results. No hardware or user settings accessed.");
        Console.WriteLine("Test artifacts: " + directory);
    }

    private static void CheckRunningHostCapability()
    {
        foreach (var mode in new[] { "old", "wrong", "current" })
        {
            using var host = new VisionMasterProcessHost();
            // A dedicated child of this test satisfies the process-alive guard; it only waits on stdin.
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fake-host-process")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            })!;
            Set(host, "_process", process);
            try
            {
                var pipeName = (string)typeof(VisionMasterProcessHost).GetField("_pipeName", Private)!.GetValue(host)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var requests = new List<string>();
                var server = Task.Run(async () =>
                {
                    for (var i = 0; i < (mode == "current" ? 2 : 1); i++)
                    {
                        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await pipe.WaitForConnectionAsync(timeout.Token);
                        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                        var command = await reader.ReadLineAsync(timeout.Token);
                        requests.Add(command!);
                        var message = i == 1 ? "0\t0\t0\t" : mode switch
                        {
                            "old" => "不支持的视觉标定命令：GET_NOZZLE_TEACHING_CAPABILITY",
                            "wrong" => "NOZZLE_LEGACY",
                            _ => "NOZZLE_MANUAL_IMAGE_V2"
                        };
                        await writer.WriteLineAsync((mode == "old" ? "ERR\t" : "OK\t") +
                            Convert.ToBase64String(Encoding.UTF8.GetBytes(message)));
                    }
                });
                try
                {
                    _ = host.RunNozzlePointInspectionAsync(timeout.Token).GetAwaiter().GetResult();
                    Require(mode == "current", "Legacy vision host must not run teaching");
                }
                catch (InvalidOperationException exception) when (mode != "current")
                {
                    Require(exception.Message.Contains("视觉程序未更新") && exception.Message.Contains(process.StartInfo.FileName),
                        "Legacy host reports its actual loaded executable path");
                }
                server.GetAwaiter().GetResult();
                Require(requests[0] == "GET_NOZZLE_TEACHING_CAPABILITY", "Check the running host, not a file inferred from build configuration");
                Require(mode != "current" || requests.SequenceEqual(new[] { "GET_NOZZLE_TEACHING_CAPABILITY", "RUN_NOZZLE_POINTS" }),
                    "A current host proceeds to teaching after the capability response");
            }
            finally
            {
                Set(host, "_process", null!);
                process.StandardInput.Close();
                if (!process.WaitForExit(5000)) process.Kill();
            }
        }
    }

    private static void CheckAlgorithmFailureRecovery()
    {
        var failure = "位置修正2(0xE0000001)；圆查找2(0xE0000512)";
        var ran = false;
        var warning = NozzleTeachingExecution.Run(() => ran = true, () => failure);
        Require(ran && warning == failure, "Procedure-level algorithm NG is retained as a warning, not thrown before rendering");
        NozzleTeachingExecution.RequireCurrentImage(true, warning);

        warning = NozzleTeachingExecution.Run(() => throw new InvalidOperationException(failure), () => "");
        Require(warning.Contains(failure), "SDK Run exception still permits current-image recovery");
        NozzleTeachingExecution.RequireCurrentImage(true, warning);
        try
        {
            NozzleTeachingExecution.RequireCurrentImage(false, warning);
            throw new Exception("Missing current image must not enable manual teaching on an old frame");
        }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains("本次未获得") && exception.Message.Contains(failure),
                "No-image failure explains why manual teaching is blocked and retains the algorithm diagnostic");
        }
        Require(NozzleTeachingExecution.Run(() => { }, () => "") == "", "Normal teaching remains successful");
    }

    private static void CheckReplacementVerification()
    {
        var page = (VisualCalibrationPage)RuntimeHelpers.GetUninitializedObject(typeof(VisualCalibrationPage));
        bool Move(VisionTargetTool tool) => (bool)Invoke(page, "RecordNozzleVerification", tool)!;
        void Save(int nozzle) => Invoke(page, "RegisterNozzleForVerification", nozzle);
        void Reset() => Invoke(page, "ResetNozzleVerification");
        string Pending() => (string)Invoke(page, "GetPendingNozzleVerificationMessage")!;

        Save(1);
        Require(Pending().Contains("待检查：吸嘴1"), "Replacing Z1 requests only Z1 verification");
        Require(!Move(VisionTargetTool.Camera), "Camera alignment cannot verify a nozzle");
        Require(!Move(VisionTargetTool.Nozzle2), "Untouched Z2 cannot complete Z1 verification");
        Require(Move(VisionTargetTool.Nozzle1), "Z1 replacement completes without requiring a new Z2 move");

        Reset();
        Save(2);
        Require(Pending().Contains("待检查：吸嘴2"), "Replacing Z2 requests only Z2 verification");
        Require(Move(VisionTargetTool.Nozzle2), "Z2 replacement completes without checking Z1");

        Reset();
        Save(1);
        Require(Move(VisionTargetTool.Nozzle1), "Only Z1 saved so far is complete");
        Save(2);
        Require(!Move(VisionTargetTool.Nozzle1), "Saving Z2 after Z1 verification requires a Z2 move");
        Require(Move(VisionTargetTool.Nozzle2), "Previously checked Z1 need not be repeated");
        Save(1);
        Require(!Move(VisionTargetTool.Nozzle2), "Resaving Z1 invalidates its earlier move check");
        Require(Move(VisionTargetTool.Nozzle1), "Resaved Z1 completes after a fresh move check");

        Reset();
        Save(1);
        Save(2);
        Require(!Move(VisionTargetTool.Nozzle2), "Saving both nozzles requires both checks");
        Require(Pending().Contains("待检查：吸嘴1"), "Dual teaching reports the remaining nozzle");
        Require(Move(VisionTargetTool.Nozzle1), "Dual teaching completes after both checks");
        Reset();
        Save(1);
        Require(Move(VisionTargetTool.Nozzle1), "Reset discards the previous session's Z2 requirement");
    }

    private static void CheckCircleGeometry()
    {
        var random = new Random(3107);
        for (var i = 0; i < 100; i++)
        {
            var expected = new Point(random.Next(300, 3000), random.Next(300, 2000));
            var radius = random.Next(20, 250);
            var angle = random.NextDouble() * Math.PI;
            Point OnCircle(double delta) => new(expected.X + radius * Math.Cos(angle + delta), expected.Y + radius * Math.Sin(angle + delta));
            var a = OnCircle(0);
            var b = OnCircle(1.2);
            var c = OnCircle(3.4);
            foreach (var points in new[] { new[] { a, b, c }, new[] { c, b, a }, new[] { b, a, c } })
            {
                Require(ThreePointCircle.TryFit(points[0], points[1], points[2], out var actual, out var actualRadius), "Valid edge points fit");
                Require((actual - expected).Length < 1e-7 && Math.Abs(actualRadius - radius) < 1e-7, "Center and radius match");
            }
        }
        Require(!ThreePointCircle.TryFit(new(1, 1), new(1, 1), new(2, 3), out _, out _), "Duplicate points rejected");
        Require(!ThreePointCircle.TryFit(new(1, 1), new(2, 2), new(3, 3), out _, out _), "Collinear points rejected");
        Require(!ThreePointCircle.TryFit(new(0, 0), new(100, 100), new(200, 200.000001), out _, out _), "Nearly collinear points rejected");
        Require(!ThreePointCircle.TryFit(new(double.NaN, 1), new(2, 2), new(3, 3), out _, out _), "Nonfinite points rejected");
    }

    private static void CheckIndependentResults(string directory)
    {
        // Isolated state containers bypass view startup, camera initialization and the shared settings singleton.
        var settings = new VisualCalibrationSettings { ActiveAxisSet = "First", ActiveCalibrationMode = "First" };
        var service = (VisionCalibrationService)RuntimeHelpers.GetUninitializedObject(typeof(VisionCalibrationService));
        Set(service, "<Settings>k__BackingField", settings);
        Set(service, "_store", new VisualCalibrationSettingsStore(Path.Combine(directory, "settings.json")));
        var page = (VisualCalibrationPage)RuntimeHelpers.GetUninitializedObject(typeof(VisualCalibrationPage));
        Set(page, "_visionCalibration", service);
        Set(page, "_uiSettings", settings);
        Set(page, "_profileStore", new VisionCalibrationProfileStore());
        var pending = new VisionBlobRectangle?[2];
        Set(page, "_pendingNozzlePoints", pending);
        Set(page, "NozzleCalibrationStatusText", new TextBlock());
        Set(page, "NozzleTeachStepText", new TextBlock());
        var xmlPath = Path.Combine(directory, "calibration.xml");
        File.WriteAllText(xmlPath, "<calibration />");
        Set(page, "CalibrationFilePathTextBox", new TextBox { Text = xmlPath });
        Set(page, "StepXPulsesTextBox", new TextBox { Text = "100000" });
        Set(page, "StepYPulsesTextBox", new TextBox { Text = "100000" });
        Set(page, "VelocityTextBox", new TextBox { Text = "200000" });
        Set(page, "SettleMillisecondsTextBox", new TextBox { Text = "300" });
        Set(page, "MovePriorityComboBox", new ComboBox());

        Invoke(page, "SetActiveNozzleOffset", VisionTargetTool.Nozzle2, 222d, -333d);
        var saved = ((string FilePath, string? BackupFilePath))Invoke(page, "SaveCurrentCalibrationProfile")!;
        var store = new VisionCalibrationProfileStore();
        var onlyZ2 = store.Load(saved.FilePath);
        Require(!onlyZ2.Nozzle1Calibrated && onlyZ2.Nozzle2Calibrated, "Z2 can be saved before Z1");
        Invoke(page, "SetActiveNozzleOffset", VisionTargetTool.Nozzle1, 111d, 444d);
        Require(settings.Nozzle2OffsetCalibrated && settings.Nozzle2OffsetXPulses == 222 && settings.Nozzle2OffsetYPulses == -333, "Saving Z1 preserves Z2");
        Invoke(page, "SaveCurrentCalibrationProfile");
        var both = store.Load(saved.FilePath);
        Require(both.Nozzle1Calibrated && both.Nozzle2Calibrated && both.Nozzle1OffsetXPulses == 111 && both.Nozzle2OffsetXPulses == 222, "Profile keeps both results");

        using var host = new VisionMasterProcessHost();
        var eventCount = 0;
        host.ManualNozzleCircleReceived += (_, e) =>
        {
            eventCount++;
            Invoke(page, "VisionHost_ManualNozzleCircleReceived", host, e);
        };
        void Send(string message)
        {
            Invoke(host, "DispatchHostEvent", message);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        Send("MANUAL_NOZZLE_CIRCLE\t1\t100\t200\t30");
        Send("MANUAL_NOZZLE_CIRCLE\t2\t300\t400\t50");
        Require(pending[0]?.X == 100 && pending[1]?.X == 300, "Separate events populate both drafts");
        var z2 = pending[1];
        Send("MANUAL_NOZZLE_CIRCLE\t1\tCLEAR");
        Require(pending[0] is null && ReferenceEquals(pending[1], z2), "Redrawing Z1 keeps Z2 draft");
        Require(settings.NozzleOffsetCalibrated && settings.Nozzle2OffsetCalibrated, "Redraw keeps previously saved results");
        Send("MANUAL_NOZZLE_CIRCLE\t1\t120\t220\t35");
        var z1 = pending[0];
        Send("MANUAL_NOZZLE_CIRCLE\t2\tCLEAR");
        Require(ReferenceEquals(pending[0], z1) && pending[1] is null, "Redrawing Z2 keeps Z1 draft");
        var validCount = eventCount;
        Send("MANUAL_NOZZLE_CIRCLE\t3\t100\t200\t30");
        Send("MANUAL_NOZZLE_CIRCLE\t1\tNaN\t200\t30");
        Send("MANUAL_NOZZLE_CIRCLE\t1\t100\t200\t-1");
        Require(eventCount == validCount, "Malformed host events are ignored");

        Invoke(page, "SetActiveNozzleOffset", VisionTargetTool.Nozzle2, -900d, 800d);
        Require(settings.NozzleOffsetCalibrated && settings.NozzleOffsetXPulses == 111 && settings.NozzleOffsetYPulses == 444, "Saving Z2 preserves Z1");
        Invoke(page, "SaveCurrentCalibrationProfile");
        var restartedSettings = new VisualCalibrationSettingsStore(Path.Combine(directory, "settings.json")).Load();
        Require(restartedSettings.NozzleOffsetCalibrated && restartedSettings.Nozzle2OffsetCalibrated && restartedSettings.NozzleOffsetXPulses == 111 && restartedSettings.Nozzle2OffsetXPulses == -900, "Saved settings reload independently");
        Invoke(page, "SetActiveNozzleOffset", VisionTargetTool.Nozzle1, 555d, -666d);
        Invoke(page, "SaveCurrentCalibrationProfile");
        var replacementProfile = store.Load(saved.FilePath);
        var replacementSettings = new VisualCalibrationSettingsStore(Path.Combine(directory, "settings.json")).Load();
        Require(replacementProfile.Nozzle1Calibrated && replacementProfile.Nozzle1OffsetXPulses == 555 && replacementProfile.Nozzle1OffsetYPulses == -666,
            "Replacement Z1 offsets are persisted to the profile");
        Require(replacementProfile.Nozzle2Calibrated && replacementProfile.Nozzle2OffsetXPulses == -900 && replacementProfile.Nozzle2OffsetYPulses == 800,
            "Replacing Z1 preserves both Z2 coordinates and its calibration flag on disk");
        Require(replacementSettings.NozzleOffsetXPulses == 555 && replacementSettings.NozzleOffsetYPulses == -666 &&
                replacementSettings.Nozzle2OffsetCalibrated && replacementSettings.Nozzle2OffsetXPulses == -900 && replacementSettings.Nozzle2OffsetYPulses == 800,
            "Restart after replacing Z1 retains the unchanged Z2 result");
        settings.NozzleOffsetCalibrated = false;
        settings.Nozzle2OffsetCalibrated = false;
        try
        {
            Invoke(page, "SaveCurrentCalibrationProfile");
            throw new Exception("Empty profile should fail");
        }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
    }

    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
