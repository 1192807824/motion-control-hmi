using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Models;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;
using ControlHub.Views;
using ControlHub.Views.Pages;

namespace MotionQaHarness;

internal static class Program
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var exitCode = 0;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            try
            {
                var mode = args.FirstOrDefault() ?? "behavior";
                if (mode.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = await CaptureScreenshotAsync(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));
                }
                else if (mode.Equals("native-load", StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = ValidateNativeLibrary();
                }
                else
                {
                    exitCode = await RunBehaviorChecksAsync();
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 1;
            }
            finally
            {
                application.Shutdown(exitCode);
            }
        };

        application.Run();
        return exitCode;
    }

    private static int ValidateNativeLibrary()
    {
        var libraryPath = Path.Combine(AppContext.BaseDirectory, "LTDMC.dll");
        var handle = NativeLibrary.Load(libraryPath);
        try
        {
            var exports = new[]
            {
                "dmc_board_init",
                "dmc_board_close",
                "dmc_get_CardInfList",
                "dmc_get_total_axes",
                "nmc_get_total_axes",
                "dmc_get_total_ionum",
                "dmc_get_total_adcnum",
                "nmc_get_errcode",
                "nmc_clear_errcode",
                "nmc_set_axis_enable",
                "nmc_set_axis_disable",
                "dmc_set_profile_unit",
                "dmc_set_s_profile",
                "dmc_set_dec_stop_time",
                "dmc_set_equiv",
                "dmc_set_emg_mode",
                "dmc_set_el_mode",
                "dmc_set_softlimit_unit",
                "dmc_set_axis_io_map",
                "dmc_set_io_dstp_mode",
                "dmc_pmove_unit",
                "dmc_vmove",
                "dmc_check_done",
                "dmc_get_position_unit",
                "dmc_get_encoder_unit",
                "dmc_get_target_position_unit",
                "dmc_read_current_speed_unit",
                "dmc_get_axis_run_mode",
                "dmc_get_stop_reason",
                "dmc_clear_stop_reason",
                "dmc_axis_io_status_ex",
                "nmc_get_axis_state_machine",
                "nmc_get_axis_errcode",
                "nmc_clear_axis_errcode",
                "nmc_set_home_profile",
                "dmc_home_move",
                "dmc_get_home_result",
                "dmc_stop",
                "dmc_emg_stop",
                "dmc_read_inport_ex",
                "dmc_read_outport_ex",
                "dmc_write_outbit",
                "dmc_get_ad_input",
                "dmc_get_da_output",
                "dmc_set_da_output"
            };
            foreach (var export in exports)
            {
                Require(NativeLibrary.TryGetExport(handle, export, out _), $"LTDMC.dll 缺少导出函数 {export}");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "passed",
                process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                library = libraryPath,
                exports
            }));
            return 0;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static async Task<int> CaptureScreenshotAsync(string? outputPath, string? stateName)
    {
        outputPath = Path.GetFullPath(outputPath ?? Path.Combine(AppContext.BaseDirectory, "motion-control.png"));
        using var scope = await OpenWindowAsync();
        Invoke(scope.Page, "SetAllServos", true);
        if (stateName?.Equals("fixed-relative", StringComparison.OrdinalIgnoreCase) == true)
        {
            InvokeEnum(scope.Page, "SetWorkbenchMode", "FixedPosition");
            scope.Page.Tuning.AbsolutePositionMode = false;
            Invoke(scope.Page, "SetPositionModeUi");
        }
        else if (stateName?.Equals("fixed-absolute", StringComparison.OrdinalIgnoreCase) == true)
        {
            InvokeEnum(scope.Page, "SetWorkbenchMode", "FixedPosition");
            scope.Page.Tuning.AbsolutePositionMode = true;
            scope.ViewModel.Axes[0].JogDistance = -12.5;
            Invoke(scope.Page, "SetPositionModeUi");
        }
        else if (stateName?.Equals("home", StringComparison.OrdinalIgnoreCase) == true)
        {
            InvokeEnum(scope.Page, "SetWorkbenchMode", "Home");
        }
        await Task.Delay(900);
        Invoke(scope.Page, "PollMotionState");
        GetPrivateField<System.Windows.Threading.DispatcherTimer>(scope.Page, "_pollTimer").Stop();
        NormalizeReferenceState(scope, stateName);
        await scope.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        var renderRoot = (FrameworkElement?)scope.Window.Content
            ?? throw new InvalidOperationException("MainWindow 没有可渲染内容。");
        renderRoot.Measure(new Size(1680, 950));
        renderRoot.Arrange(new Rect(0, 0, 1680, 950));
        renderRoot.UpdateLayout();
        renderRoot.InvalidateVisual();
        await scope.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        var bitmap = new RenderTargetBitmap(1680, 950, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(renderRoot);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine(outputPath);
        return 0;
    }

    private static void NormalizeReferenceState(WindowScope scope, string? stateName)
    {
        foreach (var axis in scope.ViewModel.Axes)
        {
            axis.State = "正常";
            axis.Homed = true;
        }

        var selectedAxis = scope.ViewModel.Axes[0];
        scope.ViewModel.SelectedAxis = selectedAxis;
        selectedAxis.Position = 10;
        selectedAxis.Target = 10;
        selectedAxis.Speed = 0;
        selectedAxis.JogSpeed = 25;

        scope.Page.Tuning.StartVelocity = 0;
        scope.Page.Tuning.StopVelocity = 0;
        scope.Page.Tuning.AccelerationMilliseconds = 100;
        scope.Page.Tuning.DecelerationMilliseconds = 100;
        scope.Page.Tuning.STimeMilliseconds = 20;
        scope.Page.Tuning.DecelerationStopMilliseconds = 100;
        scope.Page.Tuning.WaitForCompletion = true;
        scope.Page.Tuning.CompletionTimeoutMilliseconds = 5000;
        scope.Page.Tuning.CompletionTolerance = 0.01;
        scope.Page.HomeTuning.Enabled = true;
        scope.Page.HomeTuning.Mode = 33;
        scope.Page.HomeTuning.LowVelocity = 5;
        scope.Page.HomeTuning.HighVelocity = 25;
        scope.Page.HomeTuning.AccelerationMilliseconds = 100;
        scope.Page.HomeTuning.DecelerationMilliseconds = 100;
        scope.Page.HomeTuning.OffsetPosition = 0;
        scope.Page.HomeTuning.TimeoutSeconds = 5;
        scope.Page.HomeTuning.SequenceOrder = 1;

        if (stateName?.Equals("fixed-absolute", StringComparison.OrdinalIgnoreCase) == true)
        {
            var absoluteModeRadio = (RadioButton?)scope.Page.FindName("AbsoluteModeRadio")
                ?? throw new InvalidOperationException("找不到绝对位置单选框。");
            absoluteModeRadio.IsChecked = true;
            selectedAxis.JogDistance = -12.5;
            Invoke(scope.Page, "SetPositionModeUi");
        }
        else if (stateName?.Equals("fixed-relative", StringComparison.OrdinalIgnoreCase) == true)
        {
            var relativeModeRadio = (RadioButton?)scope.Page.FindName("RelativeModeRadio")
                ?? throw new InvalidOperationException("找不到相对位置单选框。");
            relativeModeRadio.IsChecked = true;
            selectedAxis.JogDistance = 10;
            Invoke(scope.Page, "SetPositionModeUi");
        }
        else if (stateName?.Equals("status-long", StringComparison.OrdinalIgnoreCase) == true)
        {
            selectedAxis.Position = -123456789.123;
            selectedAxis.Target = 987654321.987;
            selectedAxis.Speed = 12345678.901;
        }
    }

    private static async Task<int> RunBehaviorChecksAsync()
    {
        using var scope = await OpenWindowAsync();
        var page = scope.Page;
        var viewModel = scope.ViewModel;
        var axis1 = viewModel.Axes[0];
        var axis2 = viewModel.Axes[1];
        Invoke(page, "SetAllServos", true);
        await Task.Delay(100);
        Require(axis1.ServoOn && axis2.ServoOn, "全轴伺服使能失败");
        Require(
            viewModel.MotionDetectedCardsText.Contains("仿真", StringComparison.Ordinal) &&
            viewModel.MotionDetectedCardsText.Contains("当前", StringComparison.Ordinal),
            "检测卡列表或当前卡没有显示");
        axis1.StatusReadHealthy = false;
        var positionBeforeInvalidStatusJog = axis1.Position;
        Invoke(page, "StartContinuousJog", 1);
        Require(
            !axis1.IsMoving && Math.Abs(axis1.Position - positionBeforeInvalidStatusJog) <= 0.001,
            "状态读取失效后仍允许启动 JOG");
        axis1.StatusReadHealthy = true;

        Require(
            page.FindName("LowSpeedPresetButton") is null &&
            page.FindName("DebugSpeedPresetButton") is null &&
            page.FindName("HighSpeedPresetButton") is null,
            "已移除的 JOG 速度预设仍存在于界面中");
        axis1.JogSpeed = 25;

        var start = axis1.Position;
        Invoke(page, "StartContinuousJog", 1);
        await Task.Delay(140);
        await page.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        var positiveJogButton = (Button?)page.FindName("JogPositiveButton")
            ?? throw new InvalidOperationException("找不到正向 JOG 按钮。");
        Require(positiveJogButton.IsEnabled, "JOG 启动后按钮被禁用，可能丢失松开/KeyUp 停止事件");
        Invoke(page, "PollMotionState");
        Invoke(page, "OwnerWindow_Deactivated", scope.Window, EventArgs.Empty);
        await Task.Delay(100);
        Invoke(page, "PollMotionState");
        Require(axis1.Position > start, "JOG 正向运行后位置没有增加");
        Require(!axis1.IsMoving, "JOG 松开后未确认停止");

        var negativeJogButton = (Button?)page.FindName("JogNegativeButton")
            ?? throw new InvalidOperationException("找不到负向 JOG 按钮。");
        Invoke(page, "StartContinuousJogFromInput", 1, positiveJogButton);
        await Task.Delay(80);
        Invoke(page, "StopActiveJog", "方向切换回归测试");
        Invoke(page, "PollMotionState");
        Invoke(page, "StartContinuousJogFromInput", -1, negativeJogButton);
        Invoke(page, "StopActiveJogFromInput", positiveJogButton, "旧正向按钮失去焦点");
        Require(
            GetPrivateField<int?>(page, "_activeJogAxisNo") == axis1.HardwareAxisNo && axis1.IsMoving,
            "正向停止后首次负向按下被旧正向按钮的失焦事件错误停止");
        Invoke(page, "StopActiveJogFromInput", negativeJogButton, "负向按钮释放");
        Invoke(page, "PollMotionState");

        Invoke(page, "StartContinuousJogFromInput", -1, negativeJogButton);
        await Task.Delay(80);
        Invoke(page, "StopActiveJog", "反向切换回归测试");
        Invoke(page, "PollMotionState");
        Invoke(page, "StartContinuousJogFromInput", 1, positiveJogButton);
        Invoke(page, "StopActiveJogFromInput", negativeJogButton, "旧负向按钮失去焦点");
        Require(
            GetPrivateField<int?>(page, "_activeJogAxisNo") == axis1.HardwareAxisNo && axis1.IsMoving,
            "负向停止后首次正向按下被旧负向按钮的失焦事件错误停止");
        Invoke(page, "StopActiveJogFromInput", positiveJogButton, "正向按钮释放");
        Invoke(page, "PollMotionState");

        Require(
            page.FindName("JogImmediateStopButton") is Button &&
            page.FindName("FixedImmediateStopButton") is Button &&
            page.FindName("HomeImmediateStopButton") is Button,
            "三个运动模式没有全部提供单轴立即停止入口");
        Invoke(page, "StartContinuousJog", 1);
        await Task.Delay(80);
        Invoke(page, "ImmediateStop_Click", page, new RoutedEventArgs());
        Invoke(page, "PollMotionState");
        var immediateStopSnapshot = GetPrivateField<IMotionCard>(page, "_motionCard").ReadAxis(axis1.HardwareAxisNo);
        Require(
            !immediateStopSnapshot.IsMoving && immediateStopSnapshot.StopReason == 1,
            "单轴立即停止没有下发 emergency=true / dmc_stop stop_mode=1 语义");
        Invoke(page, "ClearAlarm_Click", page, new RoutedEventArgs());

        page.Tuning.WaitForCompletion = true;
        page.Tuning.AbsolutePositionMode = false;
        page.Tuning.CompletionTimeoutMilliseconds = 3000;
        axis1.JogDistance = 10;
        axis1.JogSpeed = 50;
        var relativeStart = axis1.Position;
        await InvokeTask(page, "ExecutePositionMoveAsync", 1);
        Require(Math.Abs(axis1.Position - (relativeStart + 10)) <= 0.02, "相对定位结果错误");
        Require(GetCommandState(page).Contains("运动完成", StringComparison.Ordinal), "相对定位未进入到位完成状态");

        page.Tuning.AbsolutePositionMode = true;
        axis1.JogDistance = 5;
        await InvokeTask(page, "ExecutePositionMoveAsync", 1);
        Require(Math.Abs(axis1.Position - 5) <= 0.02, "绝对定位结果错误");

        axis1.JogDistance = -5;
        await InvokeTask(page, "ExecutePositionMoveAsync", 1);
        Require(Math.Abs(axis1.Position + 5) <= 0.02, "绝对负位置定位结果错误");

        axis1.JogDistance = 0;
        await InvokeTask(page, "ExecutePositionMoveAsync", 1);
        Require(Math.Abs(axis1.Position) <= 0.02, "绝对零位定位结果错误");

        page.Tuning.WaitForCompletion = false;
        page.Tuning.AbsolutePositionMode = false;
        axis1.JogDistance = 5;
        axis1.JogSpeed = 50;
        await InvokeTask(page, "ExecutePositionMoveAsync", 1);
        await Task.Delay(250);
        Invoke(page, "PollMotionState");
        Require(!axis1.IsMoving, "不等待模式下运动结束后仍显示运行中");
        Require(GetCommandState(page).Contains("运动完成", StringComparison.Ordinal), "不等待模式未自动收尾为到位状态");

        page.Tuning.StartVelocity = 1;
        page.Tuning.CompletionTolerance = 0.02;
        Invoke(page, "MotionProfile_LostFocus", page, new RoutedEventArgs());
        viewModel.SelectedAxis = axis2;
        await Task.Delay(60);
        Require(Math.Abs(page.Tuning.StartVelocity) <= 0.001, "轴 2 错误继承了轴 1 的启动速度");
        Require(Math.Abs(page.Tuning.CompletionTolerance - 0.01) <= 0.001, "轴 2 错误继承了轴 1 的到位误差");
        page.Tuning.StartVelocity = 2;
        viewModel.SelectedAxis = axis1;
        await Task.Delay(60);
        Require(Math.Abs(page.Tuning.StartVelocity - 1) <= 0.001, "切回轴 1 后没有恢复独立启动速度");
        Require(Math.Abs(page.Tuning.CompletionTolerance - 0.02) <= 0.001, "切回轴 1 后没有恢复独立到位误差");

        page.HomeTuning.Enabled = true;
        page.HomeTuning.Mode = 41;
        page.HomeTuning.LowVelocity = 3;
        page.HomeTuning.HighVelocity = 12;
        page.HomeTuning.AccelerationMilliseconds = 150;
        page.HomeTuning.DecelerationMilliseconds = 175;
        page.HomeTuning.OffsetPosition = -2.5;
        page.HomeTuning.TimeoutSeconds = 17;
        page.HomeTuning.SequenceOrder = 2;
        Invoke(page, "HomeProfile_Changed", page, new RoutedEventArgs());
        var persistedMotionOptions = GetPrivateField<MotionCardOptionsStore>(page, "_motionOptionsStore").Load();
        var persistedAxis1Home = persistedMotionOptions.GetHomeProfile(axis1.HardwareAxisNo);
        Require(
            persistedAxis1Home.Enabled &&
            persistedAxis1Home.Mode == 41 &&
            Math.Abs(persistedAxis1Home.LowVelocity - 3) <= 0.001 &&
            Math.Abs(persistedAxis1Home.HighVelocity - 12) <= 0.001 &&
            Math.Abs(persistedAxis1Home.AccelerationSeconds - 0.15) <= 0.001 &&
            Math.Abs(persistedAxis1Home.DecelerationSeconds - 0.175) <= 0.001 &&
            Math.Abs(persistedAxis1Home.OffsetPosition + 2.5) <= 0.001 &&
            persistedMotionOptions.HomeTimeoutSeconds == 17 &&
            persistedMotionOptions.HomeSequence.Take(3).SequenceEqual(new[] { 1, 0, 2 }),
            "当前轴回零模式/速度/时间/偏移/超时没有完整持久化");
        Require(
            Math.Abs(persistedMotionOptions.GetMoveProfile(axis1.HardwareAxisNo).StartVelocity - 1) <= 0.001 &&
            Math.Abs(persistedMotionOptions.GetMoveProfile(axis1.HardwareAxisNo).CompletionTolerance - 0.02) <= 0.001,
            "当前轴运动曲线没有落盘");

        viewModel.SelectedAxis = axis2;
        await Task.Delay(60);
        Require(page.HomeTuning.Mode != 41, "轴 2 错误继承了轴 1 的独立回零模式");
        page.HomeTuning.Enabled = true;
        page.HomeTuning.Mode = 42;
        page.HomeTuning.SequenceOrder = 2;
        Invoke(page, "HomeProfile_Changed", page, new RoutedEventArgs());
        viewModel.SelectedAxis = axis1;
        await Task.Delay(60);
        Require(
            page.HomeTuning.Mode == 41 &&
            Math.Abs(page.HomeTuning.OffsetPosition + 2.5) <= 0.001 &&
            page.HomeTuning.TimeoutSeconds == 17 &&
            page.HomeTuning.SequenceOrder == 1,
            "切回轴 1 后没有恢复独立回零参数");

        var liveMotionOptions = GetPrivateField<MotionCardOptions>(page, "_motionOptions");
        var normalMotionStore = GetPrivateField<MotionCardOptionsStore>(page, "_motionOptionsStore");
        var sequenceBeforeFailedAxisSwitch = liveMotionOptions.HomeSequence.ToArray();
        SetPrivateField(page, "_motionOptionsStore", new MotionCardOptionsStore(AppContext.BaseDirectory));
        page.Tuning.StartVelocity = 9;
        page.HomeTuning.Mode = 77;
        page.HomeTuning.SequenceOrder = 2;
        viewModel.SelectedAxis = axis2;
        await Task.Delay(60);
        Require(
            Math.Abs(liveMotionOptions.GetMoveProfile(axis1.HardwareAxisNo).StartVelocity - 1) <= 0.001 &&
            liveMotionOptions.GetHomeProfile(axis1.HardwareAxisNo).Mode == 41 &&
            liveMotionOptions.HomeSequence.SequenceEqual(sequenceBeforeFailedAxisSwitch) &&
            !GetPrivateField<bool>(page, "_homeConfigurationSaveHealthy"),
            "A failed axis-switch save was not rolled back and locked.");
        Require(
            !((Button?)page.FindName("HomeCurrentButton") ?? throw new InvalidOperationException()).IsEnabled &&
            !((Button?)page.FindName("HomeAllButton") ?? throw new InvalidOperationException()).IsEnabled,
            "Homing buttons were re-enabled after a failed axis-switch save.");
        var diskOptionsAfterFailedAxisSwitch = normalMotionStore.Load();
        Require(
            Math.Abs(diskOptionsAfterFailedAxisSwitch.GetMoveProfile(axis1.HardwareAxisNo).StartVelocity - 1) <= 0.001 &&
            diskOptionsAfterFailedAxisSwitch.GetHomeProfile(axis1.HardwareAxisNo).Mode == 41 &&
            diskOptionsAfterFailedAxisSwitch.HomeSequence.SequenceEqual(sequenceBeforeFailedAxisSwitch),
            "A failed axis-switch save changed the persisted configuration.");
        SetPrivateField(page, "_motionOptionsStore", normalMotionStore);
        viewModel.SelectedAxis = axis1;
        await Task.Delay(60);
        Require(
            Math.Abs(page.Tuning.StartVelocity - 1) <= 0.001 &&
            page.HomeTuning.Mode == 41 &&
            page.HomeTuning.SequenceOrder == 1,
            "Axis-switch rollback values were not restored after the store recovered.");

        axis1.JogSpeed = 47;
        axis1.JogDistance = 12.5;
        Invoke(page, "AxisSettingsTextBox_LostFocus", page, new RoutedEventArgs());
        await page.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var persistedAxisSettings = new AxisSettingsStore().Load();
        Require(
            persistedAxisSettings.TryGetValue(axis1.AxisNo, out var persistedAxis1Settings) &&
            Math.Abs((persistedAxis1Settings.JogSpeed ?? 0) - 47) <= 0.001 &&
            Math.Abs((persistedAxis1Settings.JogDistance ?? 0) - 12.5) <= 0.001,
            "JOG 速度或定位距离没有持久化");

        page.Tuning.WaitForCompletion = true;
        axis1.JogDistance = 50;
        axis1.JogSpeed = 5;
        var axis2Start = axis2.Position;
        var longMove = InvokeTask(page, "ExecutePositionMoveAsync", 1);
        await Task.Delay(80);
        var alarmsBeforeBusyActions = viewModel.AlarmRecords.Count;
        Invoke(page, "ClearAlarm_Click", page, new RoutedEventArgs());
        Require(
            viewModel.AlarmRecords.Count > alarmsBeforeBusyActions &&
            viewModel.AlarmRecords[0].Code == "CLEAR-ALARM-BUSY",
            "运动中清报警没有被安全拦截");
        Invoke(page, "SetServo", axis1, false);
        Require(axis1.ServoOn, "运动中错误地解除了伺服使能");
        viewModel.SelectedAxis = axis2;
        Invoke(page, "LocalStop_Click", page, new RoutedEventArgs());
        await longMove;
        Invoke(page, "PollMotionState");
        Require(!axis1.IsMoving, "切轴后减速停止没有停原运动轴");
        Require(Math.Abs(axis2.Position - axis2Start) <= 0.001, "切轴后减速停止错误地移动了新选轴");
        Require(GetCommandState(page).Contains("停止", StringComparison.Ordinal), "切轴停止后命令状态未显示停止");

        var homeSequence = (AxisStatus[]?)Invoke(page, "PreflightHomeSequence")
            ?? throw new InvalidOperationException("顺序回零预检没有返回轴序列。");
        Require(homeSequence.Length == 16, "顺序回零预检没有覆盖配置的 16 轴序列");
        Invoke(page, "SetServo", axis2, false);
        var preflightRejectedDisabledAxis = false;
        try
        {
            Invoke(page, "PreflightHomeSequence");
        }
        catch (MotionCardException)
        {
            preflightRejectedDisabledAxis = true;
        }
        Require(preflightRejectedDisabledAxis, "顺序回零预检没有拒绝未使能轴");
        Invoke(page, "SetServo", axis2, true);

        static MotionAxisSnapshot CreateStoppedSnapshot(AxisStatus axis) => new(
            axis.HardwareAxisNo,
            axis.Position,
            axis.Position,
            axis.Target,
            0,
            false,
            true,
            axis.Homed,
            false,
            false,
            false,
            false,
            false,
            4,
            0,
            0,
            0);

        Invoke(page, "ArmStopConfirmation", axis1.HardwareAxisNo, true);
        Invoke(page, "ArmStopConfirmation", axis2.HardwareAxisNo, true);
        Invoke(page, "ApplySnapshot", axis1, CreateStoppedSnapshot(axis1));
        Require(
            GetCommandState(page).Contains("剩余 1 轴", StringComparison.Ordinal),
            "多轴停止确认在第一轴停止后过早显示全部停止");
        Invoke(page, "ApplySnapshot", axis2, CreateStoppedSnapshot(axis2));
        Require(GetCommandState(page).Contains("全部停止", StringComparison.Ordinal), "全部轴确认停止后未进入完成状态");

        Invoke(page, "StartHomeTracking", axis1.HardwareAxisNo, DateTime.UtcNow.AddSeconds(10));
        Invoke(page, "ApplySnapshot", axis1, CreateStoppedSnapshot(axis1) with { Homed = false, StopReason = 9 });
        Require(
            GetPrivateField<Dictionary<int, DateTime>>(page, "_homeDeadlines").Count == 0 &&
            viewModel.AlarmRecords.Any(record => record.Code == $"AXIS-{axis1.AxisNo:00}-HOME-FAILED"),
            "单轴回零已停止但未回零时没有立即判失败");

        Invoke(page, "ArmStopConfirmation", axis1.HardwareAxisNo, false);
        Invoke(page, "EnforceSafetyDeadlines", DateTime.UtcNow.AddMinutes(1));
        Require(
            viewModel.AlarmRecords.Any(record => record.Code.Contains("STOP-CONFIRM-TIMEOUT", StringComparison.Ordinal)),
            "单轴停止确认超时没有升级全轴急停并记录报警");
        Invoke(page, "PollMotionState");
        Require(GetPrivateField<HashSet<int>>(page, "_pendingStopAxisNos").Count == 0, "全轴急停后停止确认没有正常收尾");

        var axisSettingsTestPath = Path.Combine(AppContext.BaseDirectory, "axis-settings-qa.json");
        var axisSettingsBackupPath = axisSettingsTestPath + ".bak";
        try
        {
            File.Delete(axisSettingsTestPath);
            File.Delete(axisSettingsBackupPath);
            var axisSettingsStore = new AxisSettingsStore(axisSettingsTestPath);
            axisSettingsStore.Save(viewModel.Axes);
            axis1.JogSpeed = 33;
            axisSettingsStore.Save(viewModel.Axes);
            var validJogSpeed = axis1.JogSpeed;
            var validJogDistance = axis1.JogDistance;
            try
            {
                axis1.JogSpeed = double.NaN;
                var rejectedNonFiniteSpeed = false;
                try
                {
                    axisSettingsStore.Save(viewModel.Axes);
                }
                catch (InvalidDataException)
                {
                    rejectedNonFiniteSpeed = true;
                }

                Require(rejectedNonFiniteSpeed, "Axis settings accepted a non-finite JOG speed.");

                axis1.JogSpeed = validJogSpeed;
                axis1.JogDistance = double.PositiveInfinity;
                var rejectedNonFiniteDistance = false;
                try
                {
                    axisSettingsStore.Save(viewModel.Axes);
                }
                catch (InvalidDataException)
                {
                    rejectedNonFiniteDistance = true;
                }

                Require(rejectedNonFiniteDistance, "Axis settings accepted a non-finite move value.");
            }
            finally
            {
                axis1.JogSpeed = validJogSpeed;
                axis1.JogDistance = validJogDistance;
            }
            Require(File.Exists(axisSettingsBackupPath), "轴参数原子保存没有生成备份");
            File.WriteAllText(axisSettingsTestPath, "{ broken json");
            var recoveredSettings = axisSettingsStore.LoadWithDiagnostics();
            Require(
                recoveredSettings.Warning?.Contains("备份", StringComparison.Ordinal) == true &&
                recoveredSettings.Settings.Count == 16,
                "轴参数主文件损坏后没有从原子备份恢复");
        }
        finally
        {
            File.Delete(axisSettingsTestPath);
            File.Delete(axisSettingsBackupPath);
        }

        var motionSettingsTestPath = Path.Combine(AppContext.BaseDirectory, "motion-settings-qa.json");
        var motionSettingsBackupPath = motionSettingsTestPath + ".bak";
        try
        {
            File.Delete(motionSettingsTestPath);
            File.Delete(motionSettingsBackupPath);
            var motionSettingsStore = new MotionCardOptionsStore(motionSettingsTestPath);
            var firstMotionOptions = new MotionCardOptions
            {
                SimulationMode = true,
                AxisCount = 2,
                HomeTimeoutSeconds = 20
            };
            motionSettingsStore.Save(firstMotionOptions);
            firstMotionOptions.HomeTimeoutSeconds = 30;
            motionSettingsStore.Save(firstMotionOptions);
            Require(File.Exists(motionSettingsBackupPath), "运动配置原子保存没有生成备份");
            File.WriteAllText(motionSettingsTestPath, "{ broken json");
            var recoveredMotionOptions = motionSettingsStore.Load();
            Require(
                recoveredMotionOptions.HomeTimeoutSeconds == 20,
                "运动配置主文件损坏后没有从原子备份恢复");
        }
        finally
        {
            File.Delete(motionSettingsTestPath);
            File.Delete(motionSettingsBackupPath);
        }

        var originalMotionCard = GetPrivateField<IMotionCard>(page, "_motionCard");
        var faultInjectingCard = new FaultInjectingMotionCard(originalMotionCard, additionalStoppedAxes: 1);
        SetPrivateField(page, "_motionCard", faultInjectingCard);

        faultInjectingCard.HoldOrdinaryStops = true;
        viewModel.SelectedAxis = axis1;
        Invoke(page, "StartContinuousJog", 1);
        await Task.Delay(80);
        Invoke(page, "LocalStop_Click", page, new RoutedEventArgs());
        Require(
            GetPrivateField<HashSet<int>>(page, "_pendingStopAxisNos").Contains(axis1.HardwareAxisNo) && axis1.IsMoving,
            "QA 前提失败：普通减速停止没有保持在停止确认中");
        var setAllCallsBeforeBusyEnable = faultInjectingCard.SetAllServosCalls;
        Invoke(page, "SetAllServos", true);
        Require(
            faultInjectingCard.SetAllServosCalls == setAllCallsBeforeBusyEnable &&
            viewModel.AlarmRecords.Any(record => record.Code == "SERVO-ALL-ON-BUSY"),
            "停止确认期间仍允许下发全轴使能");
        Invoke(page, "ImmediateStop_Click", page, new RoutedEventArgs());
        Require(
            faultInjectingCard.ImmediateStopCalls == 1 &&
            !axis1.IsMoving &&
            !GetPrivateField<HashSet<int>>(page, "_pendingStopAxisNos").Contains(axis1.HardwareAxisNo),
            "普通减速停止确认中点击单轴立即停没有升级下发 Stop(true)");
        faultInjectingCard.HoldOrdinaryStops = false;

        InvokeEnum(page, "SetWorkbenchMode", "FixedPosition");
        page.Tuning.AbsolutePositionMode = false;
        page.Tuning.WaitForCompletion = false;
        axis1.JogDistance = 50;
        axis1.JogSpeed = 5;
        await InvokeTask(page, "ExecutePositionMoveAsync", 1);
        await Task.Delay(60);
        faultInjectingCard.FailNextStop = true;
        Invoke(page, "ImmediateStop_Click", page, new RoutedEventArgs());
        Require(
            GetCommandState(page).Contains("异常", StringComparison.Ordinal) ||
            GetCommandState(page).Contains("失败", StringComparison.Ordinal),
            "单轴停止失败升级全轴急停后，被错误覆盖为普通停止成功状态");
        Require(
            !axis1.State.Equals("已手动减速停止", StringComparison.Ordinal) &&
            !axis1.State.Equals("已手动立即停止", StringComparison.Ordinal),
            "停止升级失败状态被操作员停止分支覆盖");

        faultInjectingCard.IgnoreEmergencyStop = true;
        Invoke(page, "StartContinuousJog", 1);
        await Task.Delay(80);
        Invoke(page, "StopAll_Click", page, new RoutedEventArgs());
        Invoke(page, "EnforceSafetyDeadlines", DateTime.UtcNow.AddMinutes(1));
        Require(
            GetPrivateField<bool>(page, "_motionSafetyLock"),
            "全轴急停下发后未在期限内确认停止，没有建立持久运动安全锁");

        faultInjectingCard.IgnoreEmergencyStop = false;
        faultInjectingCard.FailEmergencyStop = true;
        Invoke(page, "StopAll_Click", page, new RoutedEventArgs());
        Require(
            viewModel.AlarmRecords.Any(record => record.Code == "EMERGENCY-STOP-FAILED"),
            "全轴急停下发失败没有记录锁定报警");
        Require(
            GetPrivateField<HashSet<int>>(page, "_pendingStopAxisNos").Count == 17,
            "急停失败后的停止确认没有覆盖界面之外的全部硬件轴");
        Require(viewModel.Axes.All(axis => !axis.StatusReadHealthy), "运动安全锁未禁用轴运动入口");
        InvokeEnum(page, "SetWorkbenchMode", "FixedPosition");
        Require(GetCommandState(page).Contains("锁定", StringComparison.Ordinal), "切换模式错误地覆盖了运动安全锁状态");

        var positionBeforeSafetyLockedJog = axis1.Position;
        Invoke(page, "StartContinuousJog", 1);
        Require(
            Math.Abs(axis1.Position - positionBeforeSafetyLockedJog) <= 0.001,
            "运动安全锁建立后仍能下发 JOG 命令");

        faultInjectingCard.FailEmergencyStop = false;
        Invoke(page, "StopAll_Click", page, new RoutedEventArgs());
        Invoke(page, "PollMotionState");
        Require(
            GetPrivateField<HashSet<int>>(page, "_pendingStopAxisNos").Count == 0,
            "全轴急停重试后未确认界面外硬件轴停止");
        Require(GetPrivateField<bool>(page, "_motionSafetyLock"), "急停重试成功后错误地自动解除安全锁");

        faultInjectingCard.FailEmergencyStop = true;
        Require(
            !page.TryShutdown(out var shutdownFailure) && originalMotionCard.IsOpen,
            "退出时急停失败仍关闭了控制卡");
        Require(
            shutdownFailure.Contains("窗口不会关闭", StringComparison.Ordinal),
            "退出安全阻断没有返回明确提示");
        faultInjectingCard.FailEmergencyStop = false;
        viewModel.SelectedAxis = axis1;
        await Task.Delay(60);
        axis1.JogSpeed = 47;
        axis1.JogDistance = 12.5;
        page.HomeTuning.Enabled = true;
        page.HomeTuning.Mode = 41;
        page.HomeTuning.LowVelocity = 3;
        page.HomeTuning.HighVelocity = 12;
        page.HomeTuning.AccelerationMilliseconds = 150;
        page.HomeTuning.DecelerationMilliseconds = 175;
        page.HomeTuning.OffsetPosition = -2.5;
        page.HomeTuning.TimeoutSeconds = 17;
        page.HomeTuning.SequenceOrder = 2;
        page.Tuning.StartVelocity = 1;
        page.Tuning.StopVelocity = 2;
        page.Tuning.AccelerationMilliseconds = 120;
        page.Tuning.DecelerationMilliseconds = 130;
        page.Tuning.STimeMilliseconds = 40;
        page.Tuning.DecelerationStopMilliseconds = 140;
        page.Tuning.WaitForCompletion = false;
        page.Tuning.CompletionTimeoutMilliseconds = 4321;
        page.Tuning.CompletionTolerance = 0.025;
        page.Tuning.AbsolutePositionMode = true;
        Require(page.TryShutdown(out _), "恢复急停后无法安全确认停稳并关闭控制卡");

        using (var reopenedScope = await OpenWindowAsync())
        {
            var reopenedAxis1 = reopenedScope.ViewModel.Axes[0];
            Require(
                Math.Abs(reopenedAxis1.JogSpeed - 47) <= 0.001 &&
                Math.Abs(reopenedAxis1.JogDistance - 12.5) <= 0.001,
                "关闭并重新打开后没有恢复 JOG 速度和定位距离");
            Require(
                Math.Abs(reopenedScope.Page.Tuning.StartVelocity - 1) <= 0.001 &&
                Math.Abs(reopenedScope.Page.Tuning.StopVelocity - 2) <= 0.001 &&
                Math.Abs(reopenedScope.Page.Tuning.AccelerationMilliseconds - 120) <= 0.001 &&
                Math.Abs(reopenedScope.Page.Tuning.DecelerationMilliseconds - 130) <= 0.001 &&
                Math.Abs(reopenedScope.Page.Tuning.STimeMilliseconds - 40) <= 0.001 &&
                Math.Abs(reopenedScope.Page.Tuning.DecelerationStopMilliseconds - 140) <= 0.001 &&
                !reopenedScope.Page.Tuning.WaitForCompletion &&
                reopenedScope.Page.Tuning.CompletionTimeoutMilliseconds == 4321 &&
                Math.Abs(reopenedScope.Page.Tuning.CompletionTolerance - 0.025) <= 0.001 &&
                reopenedScope.Page.Tuning.AbsolutePositionMode,
                "Close/reopen did not restore every move-profile input.");
            Require(
                reopenedScope.Page.HomeTuning.Enabled &&
                reopenedScope.Page.HomeTuning.Mode == 41 &&
                Math.Abs(reopenedScope.Page.HomeTuning.LowVelocity - 3) <= 0.001 &&
                Math.Abs(reopenedScope.Page.HomeTuning.HighVelocity - 12) <= 0.001 &&
                Math.Abs(reopenedScope.Page.HomeTuning.AccelerationMilliseconds - 150) <= 0.001 &&
                Math.Abs(reopenedScope.Page.HomeTuning.DecelerationMilliseconds - 175) <= 0.001 &&
                Math.Abs(reopenedScope.Page.HomeTuning.OffsetPosition + 2.5) <= 0.001 &&
                reopenedScope.Page.HomeTuning.TimeoutSeconds == 17 &&
                reopenedScope.Page.HomeTuning.SequenceOrder == 2,
                "关闭并重新打开后没有恢复当前轴回零参数");
        }

        using (var invalidBindingScope = await OpenWindowAsync())
        {
            Invoke(invalidBindingScope.Page, "SetAllServos", true);
            var startVelocityTextBox = FindVisualChildren<TextBox>(invalidBindingScope.Page)
                .FirstOrDefault(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path
                        .EndsWith("StartVelocity", StringComparison.Ordinal) == true)
                ?? throw new InvalidOperationException("Start-velocity input was not found.");
            startVelocityTextBox.Text = "abc";
            startVelocityTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Require(
                Validation.GetHasError(startVelocityTextBox) &&
                !(bool)(Invoke(invalidBindingScope.Page, "TryCommitPersistentInputBindings") ?? true),
                "A conversion-error TextBox was not detected before shutdown.");
            Require(
                !invalidBindingScope.Page.TryShutdown(out var bindingFailure) &&
                bindingFailure.Contains("输入参数保存失败", StringComparison.Ordinal),
                "A conversion-error TextBox was silently discarded on shutdown.");
            startVelocityTextBox.Text = "1";
            startVelocityTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Require(
                !Validation.GetHasError(startVelocityTextBox) &&
                invalidBindingScope.Page.TryShutdown(out _),
                "Shutdown did not recover after the conversion-error input was corrected.");
        }

        using (var invalidSettingsScope = await OpenWindowAsync())
        {
            Invoke(invalidSettingsScope.Page, "SetAllServos", true);
            invalidSettingsScope.Page.HomeTuning.Mode = -1;
            Invoke(invalidSettingsScope.Page, "HomeProfile_Changed", invalidSettingsScope.Page, new RoutedEventArgs());
            var homeCurrentButton = (Button?)invalidSettingsScope.Page.FindName("HomeCurrentButton")
                ?? throw new InvalidOperationException("HomeCurrentButton was not found.");
            var homeAllButton = (Button?)invalidSettingsScope.Page.FindName("HomeAllButton")
                ?? throw new InvalidOperationException("HomeAllButton was not found.");
            Require(
                !homeCurrentButton.IsEnabled &&
                !homeAllButton.IsEnabled &&
                !(bool)(Invoke(invalidSettingsScope.Page, "TrySaveHomeSettingsBeforeMotion") ?? true),
                "Invalid home input did not block both home commands.");
            Require(
                !invalidSettingsScope.Page.TryShutdown(out var settingsFailure) &&
                settingsFailure.Contains("输入参数保存失败", StringComparison.Ordinal),
                "无效回零参数仍允许静默关闭并丢失输入");
            invalidSettingsScope.Page.HomeTuning.Mode = 41;
            Invoke(invalidSettingsScope.Page, "HomeProfile_Changed", invalidSettingsScope.Page, new RoutedEventArgs());
            Require(
                invalidSettingsScope.Page.TryShutdown(out _),
                "修正无效回零参数后仍无法保存并关闭");
        }

        var result = new
        {
            result = "passed",
            jog_position = axis1.Position,
            relative_absolute_no_wait = "passed",
            absolute_negative_and_zero = "passed",
            per_axis_profiles = "passed",
            home_profile_editor_persistence = "passed",
            jog_distance_speed_persistence = "passed",
            motion_settings_backup_recovery = "passed",
            reopen_restores_all_inputs = "passed",
            invalid_settings_block_close = "passed",
            binding_errors_block_close = "passed",
            axis_switch_save_rollback = "passed",
            switch_axis_stop = "passed",
            jog_release_input = "passed",
            single_axis_immediate_stop = "passed",
            busy_clear_alarm_and_servo_off = "passed",
            home_sequence_preflight = "passed",
            jog_direction_reversal = "passed",
            detected_card_list = "passed",
            stale_status_motion_lock = "passed",
            single_axis_home_failure = "passed",
            stop_confirmation_watchdog = "passed",
            multi_axis_stop_confirmation = "passed",
            immediate_stop_escalates_pending_deceleration = "passed",
            operator_stop_failure_state_preserved = "passed",
            emergency_confirmation_timeout_lock = "passed",
            servo_enable_pending_guard = "passed",
            hidden_hardware_axis_confirmation = "passed",
            emergency_failure_motion_lock = "passed",
            shutdown_stop_confirmation = "passed",
            atomic_axis_settings = "passed",
            non_finite_axis_settings_rejected = "passed",
            axis2_position = axis2.Position
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }

    private static async Task<WindowScope> OpenWindowAsync()
    {
        var window = new MainWindow
        {
            Width = 1680,
            Height = 950,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            WindowState = WindowState.Normal
        };
        window.Show();
        await Task.Delay(350);
        var page = (MotionControlPage?)window.FindName("MotionPage")
            ?? throw new InvalidOperationException("找不到 MotionPage。");
        var viewModel = (MainWindowViewModel?)window.DataContext
            ?? throw new InvalidOperationException("MainWindowViewModel 未初始化。");
        Require(viewModel.Axes.Count == 16, $"期望 16 轴，实际 {viewModel.Axes.Count} 轴");
        return new WindowScope(window, page, viewModel);
    }

    private static object? Invoke(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, PrivateInstance)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static Task InvokeTask(object target, string methodName, params object?[] arguments)
    {
        return (Task?)Invoke(target, methodName, arguments)
            ?? throw new InvalidOperationException($"{methodName} 未返回 Task。");
    }

    private static object? InvokeEnum(object target, string methodName, string enumValue)
    {
        var method = target.GetType().GetMethod(methodName, PrivateInstance)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        var parameterType = method.GetParameters().Single().ParameterType;
        return Invoke(target, methodName, Enum.Parse(parameterType, enumValue));
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        return (T?)target.GetType().GetField(fieldName, PrivateInstance)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, PrivateInstance)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string GetCommandState(MotionControlPage page)
    {
        return ((TextBlock?)page.FindName("CommandStateText"))?.Text
            ?? throw new InvalidOperationException("找不到 CommandStateText。");
    }

    private sealed class WindowScope(MainWindow window, MotionControlPage page, MainWindowViewModel viewModel) : IDisposable
    {
        public MainWindow Window { get; } = window;
        public MotionControlPage Page { get; } = page;
        public MainWindowViewModel ViewModel { get; } = viewModel;

        public void Dispose()
        {
            Window.Close();
        }
    }

    private sealed class FaultInjectingMotionCard(IMotionCard inner, int additionalStoppedAxes) : IMotionCard
    {
        public bool FailEmergencyStop { get; set; }
        public bool IgnoreEmergencyStop { get; set; }
        public bool HoldOrdinaryStops { get; set; }
        public bool FailNextStop { get; set; }
        public int ImmediateStopCalls { get; private set; }
        public int SetAllServosCalls { get; private set; }
        public bool IsOpen => inner.IsOpen;
        public int AxisCount => inner.AxisCount + additionalStoppedAxes;
        public int DigitalInputCount => inner.DigitalInputCount;
        public int DigitalOutputCount => inner.DigitalOutputCount;
        public int AnalogInputCount => inner.AnalogInputCount;
        public int AnalogOutputCount => inner.AnalogOutputCount;
        public MotionCardConnectionInfo Open() => inner.Open();
        public void Close() => inner.Close();
        public ushort ReadBusErrorCode() => inner.ReadBusErrorCode();

        public MotionAxisSnapshot ReadAxis(int hardwareAxisNo)
        {
            if (hardwareAxisNo < inner.AxisCount)
            {
                return inner.ReadAxis(hardwareAxisNo);
            }

            if (hardwareAxisNo >= AxisCount)
            {
                throw new ArgumentOutOfRangeException(nameof(hardwareAxisNo));
            }

            return new MotionAxisSnapshot(
                hardwareAxisNo,
                0,
                0,
                0,
                0,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                1,
                0,
                0,
                0);
        }

        public uint ReadDigitalInputs(int portNo) => inner.ReadDigitalInputs(portNo);
        public uint ReadDigitalOutputs(int portNo) => inner.ReadDigitalOutputs(portNo);
        public void WriteDigitalOutput(int bitNo, bool enabled) => inner.WriteDigitalOutput(bitNo, enabled);
        public double ReadAnalogInput(int channel) => inner.ReadAnalogInput(channel);
        public double ReadAnalogOutput(int channel) => inner.ReadAnalogOutput(channel);
        public void WriteAnalogOutput(int channel, double value) => inner.WriteAnalogOutput(channel, value);
        public void ServoOn(int hardwareAxisNo, bool enabled) => inner.ServoOn(hardwareAxisNo, enabled);
        public void SetAllServos(bool enabled)
        {
            SetAllServosCalls++;
            inner.SetAllServos(enabled);
        }
        public void Home(int hardwareAxisNo) => inner.Home(hardwareAxisNo);
        public void Jog(int hardwareAxisNo, double velocity) => inner.Jog(hardwareAxisNo, velocity);
        public void MoveRelative(int hardwareAxisNo, double distance, double velocity) => inner.MoveRelative(hardwareAxisNo, distance, velocity);
        public void MoveAbsolute(int hardwareAxisNo, double position, double velocity) => inner.MoveAbsolute(hardwareAxisNo, position, velocity);
        public void Stop(int hardwareAxisNo, bool emergency = false)
        {
            if (emergency)
            {
                ImmediateStopCalls++;
            }

            if (!emergency && HoldOrdinaryStops)
            {
                return;
            }

            if (FailNextStop)
            {
                FailNextStop = false;
                throw new MotionCardException("QA 注入：单轴停止下发失败。");
            }

            inner.Stop(hardwareAxisNo, emergency);
        }

        public void EmergencyStop()
        {
            if (FailEmergencyStop)
            {
                throw new MotionCardException("QA 注入：全轴急停下发失败。");
            }

            if (IgnoreEmergencyStop)
            {
                return;
            }

            inner.EmergencyStop();
        }

        public void ClearAlarms(IEnumerable<int> hardwareAxisNumbers) => inner.ClearAlarms(hardwareAxisNumbers);
        public void Dispose()
        {
            // The wrapped card remains owned by MotionControlPage.
        }
    }
}
