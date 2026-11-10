using System.Windows.Threading;

namespace ControlHub.Services.Diagnostics;

/// <summary>Bounded, lossy UI log preview. Never use for commands, responses used by protocols, or alarms.</summary>
internal sealed class DispatcherLogBuffer(Dispatcher dispatcher, Action<string> consume, int capacity = 300) : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private bool _scheduled, _disposed;
    private long _omitted;

    public void Post(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Bound both the entry count and the size of a diagnostic preview.
            if (message.Length > 2048) message = message[..2048] + "…（显示已截断）";
            if (_pending.Count >= Math.Max(1, capacity)) { _pending.Dequeue(); _omitted++; }
            _pending.Enqueue(message);
            if (_scheduled) return;
            _scheduled = true;
            Schedule();
        }
    }

    private void Schedule()
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { Dispose(); return; }
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
    }

    private void Drain()
    {
        try
        {
            long omitted;
            lock (_gate)
            {
                if (_disposed) return;
                omitted = _omitted;
                _omitted = 0;
            }
            if (omitted > 0) consume($"界面日志繁忙，已省略 {omitted} 条较早的接收预览（不影响仪表响应处理和测量保存）");
            for (var i = 0; i < 32; i++)
            {
                string message;
                lock (_gate)
                {
                    if (_disposed || !_pending.TryDequeue(out message!)) return;
                }
                consume(message);
            }
        }
        finally
        {
            lock (_gate)
            {
                _scheduled = false;
                if (!_disposed && _pending.Count > 0) { _scheduled = true; Schedule(); }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _pending.Clear(); _omitted = 0; }
    }
}
