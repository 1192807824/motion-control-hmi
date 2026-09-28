using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ControlHub.Services.Motion;

public sealed record PressureSafetyTrip(string Reason, Exception? StopError, double StopCallMilliseconds);

/// <summary>软件压力保护。1 ms 是目标轮询等待时间，不是 Windows/SDK/机械停止的硬实时保证。</summary>
public sealed class ZAxisPressureSafety
{
    private int _threshold;
    private int _verifiedThreshold;
    public int Threshold => Volatile.Read(ref _threshold);

    public ZAxisPressureSafety(int threshold = MotionCardOptions.DefaultZPressureEmergencyStopThreshold)
    {
        ConfigureThreshold(threshold);
    }

    public void ConfigureThreshold(int threshold)
    {
        MotionCardOptions.ValidateZPressureEmergencyStopThreshold(threshold);
        // 不等待采集锁，不清除已触发的锁定；下一轮采集使用新阈值。
        Volatile.Write(ref _threshold, threshold);
    }
    public const int PollMilliseconds = 1;
    public const int MaximumFeedbackAgeMilliseconds = 100;
    private static readonly (int Axis, string Label)[] Axes =
        [(5, "第一套 Z1"), (7, "第一套 Z2"), (9, "第二套 Z1"), (11, "第二套 Z2")];
    private readonly object _scanGate = new();
    private ZAxisPressureReading[] _readings = ZAxisPressureMonitor.Unavailable("保护初始化");
    private string? _tripReason;
    private long _lastHealthyScan;
    private bool _stopSucceeded;
    private bool _notified;
    private long _lastStopAttempt;
    private Thread? _thread;
    private readonly CancellationTokenSource _cancellation = new();

    public string? TripReason => Volatile.Read(ref _tripReason);
    public string Status
    {
        get
        {
            if (TripReason is not null) return "压力保护已锁定 · 排障后重启";
            var stamp = Interlocked.Read(ref _lastHealthyScan);
            if (stamp == 0 || Volatile.Read(ref _verifiedThreshold) != Threshold) return "压力保护校验中";
            return Stopwatch.GetElapsedTime(stamp).TotalMilliseconds > MaximumFeedbackAgeMilliseconds
                ? "压力反馈过期 · 禁止启动" : $"保护中 · 原始值 > {Threshold} 全轴急停";
        }
    }

    public ZAxisPressureReading[] Readings =>
        Interlocked.Read(ref _lastHealthyScan) is var stamp && stamp != 0 &&
        Stopwatch.GetElapsedTime(stamp).TotalMilliseconds > MaximumFeedbackAgeMilliseconds
            ? ZAxisPressureMonitor.Unavailable("反馈过期", TripReason ?? "保护采集未及时更新。")
            : Volatile.Read(ref _readings).ToArray();

    public void ThrowIfMotionBlocked()
    {
        if (TripReason is { } reason)
            throw new MotionCardException($"压力保护已锁定：{reason}。排除故障后重启程序。", "Z压力保护");
        var stamp = Interlocked.Read(ref _lastHealthyScan);
        if (_cancellation.IsCancellationRequested || stamp == 0 || Volatile.Read(ref _verifiedThreshold) != Threshold ||
            Stopwatch.GetElapsedTime(stamp).TotalMilliseconds > MaximumFeedbackAgeMilliseconds)
            throw new MotionCardException("四个Z轴压力尚未获得有效实时反馈，禁止启动运动。", "Z压力保护");
    }

    public void Start(IMotionCard card, Action emergencyStop, Action<PressureSafetyTrip> notify)
    {
        if (_thread is not null) return;
        _thread = new Thread(() =>
        {
            // 降低 Windows 定时等待粒度；仍不能保证每轮采样或停机在 1 ms 内完成。
            var timerRequested = timeBeginPeriod(1) == 0;
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    CheckOnce(card, emergencyStop, notify);
                    if (_cancellation.Token.WaitHandle.WaitOne(PollMilliseconds)) break;
                }
            }
            finally
            {
                if (timerRequested) timeEndPeriod(1);
            }
        }) { IsBackground = true, Name = "ZAxisPressureSafety", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop() => _cancellation.Cancel();

    public void CheckOnce(IMotionCard card, Action emergencyStop, Action<PressureSafetyTrip> notify)
    {
        lock (_scanGate)
        {
            if (_cancellation.IsCancellationRequested) return;
            var started = Stopwatch.GetTimestamp();
            // 同一批次采用同一阈值，改值后必须重新完成四轴校验才能启动运动。
            var threshold = Threshold;
            try
            {
                var previous = Interlocked.Read(ref _lastHealthyScan);
                if (previous != 0 && Stopwatch.GetElapsedTime(previous).TotalMilliseconds > MaximumFeedbackAgeMilliseconds)
                    Trip("压力反馈超过100 ms未完成有效更新", emergencyStop, notify);
                var readings = new ZAxisPressureReading[Axes.Length];
                for (var index = 0; index < Axes.Length; index++)
                {
                    if (_cancellation.IsCancellationRequested) return;
                    var (axis, label) = Axes[index];
                    var value = card.ReadActualTorque(axis);
                    // 每轴读取后立即判定，不等剩余轴、总线检查或界面刷新。
                    if (value > threshold)
                        Trip($"{label}（轴{axis}）压力原始值 {value} > {threshold}", emergencyStop, notify);
                    readings[index] = new(axis, label, value, value > threshold ? "超限" : "实时", $"6077 原始反馈；超过{threshold}触发全轴急停。");
                    if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > MaximumFeedbackAgeMilliseconds)
                        Trip("压力采集耗时超过100 ms，无法维持有效监控", emergencyStop, notify);
                }
                var busError = card.ReadBusErrorCode();
                if (busError != 0 && busError != 0x0228)
                    throw new MotionCardException($"EtherCAT 总线错误 0x{busError:X4}");
                if (!card.IsOpen) throw new MotionCardException("控制卡连接已断开");
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > MaximumFeedbackAgeMilliseconds)
                    Trip("压力采集耗时超过100 ms，无法维持有效监控", emergencyStop, notify);
                Volatile.Write(ref _readings, readings);
                Interlocked.Exchange(ref _lastHealthyScan, Stopwatch.GetTimestamp());
                Volatile.Write(ref _verifiedThreshold, threshold);
            }
            catch (Exception exception)
            {
                if (_cancellation.IsCancellationRequested) return;
                Volatile.Write(ref _readings, ZAxisPressureMonitor.Unavailable("保护读取失败", exception.Message));
                Trip($"压力保护读取失败：{exception.Message}", emergencyStop, notify);
            }
            if (TripReason is { } reason && !_stopSucceeded)
                Trip(reason, emergencyStop, notify);
        }
    }

    private void Trip(string reason, Action emergencyStop, Action<PressureSafetyTrip> notify)
    {
        // 先锁运动入口，再下发急停；恢复正常读数也不会自动解除锁定。
        Interlocked.CompareExchange(ref _tripReason, reason, null);
        if (_stopSucceeded || (_lastStopAttempt != 0 && Stopwatch.GetElapsedTime(_lastStopAttempt).TotalMilliseconds < 10)) return;
        _lastStopAttempt = Stopwatch.GetTimestamp();
        Exception? error = null;
        try { emergencyStop(); _stopSucceeded = true; }
        catch (Exception exception) { error = exception; }
        var elapsed = Stopwatch.GetElapsedTime(_lastStopAttempt).TotalMilliseconds;
        // 失败后持续重试，UI只接收首次结果以及重试成功，避免报警队列堆积。
        if (!_notified || _stopSucceeded)
        {
            _notified = true;
            try { notify(new(TripReason!, error, elapsed)); }
            catch { /* 报警显示失败不得终止保护线程。 */ }
        }
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint period);
}
