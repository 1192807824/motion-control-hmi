namespace ControlHub.Services.Motion;

public enum MotionCardAccessPriority { Normal, Pressure, EmergencyStop }

/// <summary>串行访问非并发SDK；释放时先服务急停和压力采集。不能抢占正在执行的原生调用。</summary>
public sealed class MotionCardAccessGate
{
    private readonly object _state = new();
    private readonly int[] _waiting = new int[3];
    private int _owner;
    private int _depth;
    private MotionCardAccessPriority _priority;

    public IDisposable Enter(MotionCardAccessPriority priority = MotionCardAccessPriority.Normal)
    {
        Acquire(priority);
        return new Lease(this);
    }

    private void Acquire(MotionCardAccessPriority priority)
    {
        if (priority is < MotionCardAccessPriority.Normal or > MotionCardAccessPriority.EmergencyStop)
            throw new ArgumentOutOfRangeException(nameof(priority));
        var thread = Environment.CurrentManagedThreadId;
        lock (_state)
        {
            if (_owner == thread) { _depth++; return; }
            _waiting[(int)priority]++;
            try
            {
                while (_owner != 0 || HasHigherWaiter(priority)) Monitor.Wait(_state);
                _owner = thread;
                _depth = 1;
                _priority = priority;
            }
            finally
            {
                _waiting[(int)priority]--;
                Monitor.PulseAll(_state);
            }
        }
    }

    private bool HasHigherWaiter(MotionCardAccessPriority priority)
    {
        for (var index = (int)priority + 1; index < _waiting.Length; index++)
            if (_waiting[index] != 0) return true;
        return false;
    }

    private void Release()
    {
        lock (_state)
        {
            EnsureOwner();
            if (--_depth != 0) return;
            _owner = 0;
            Monitor.PulseAll(_state);
        }
    }

    /// <summary>使能等待期间释放全部重入层级，重新排队时仍遵守安全优先级。</summary>
    public void SleepOutside(int milliseconds)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        int depth;
        MotionCardAccessPriority priority;
        lock (_state)
        {
            EnsureOwner();
            depth = _depth;
            priority = _priority;
            _owner = _depth = 0;
            Monitor.PulseAll(_state);
        }
        try { Thread.Sleep(milliseconds); }
        finally
        {
            Acquire(priority);
            lock (_state) _depth = depth;
        }
    }

    private void EnsureOwner()
    {
        if (_owner != Environment.CurrentManagedThreadId) throw new SynchronizationLockException();
    }

    private sealed class Lease(MotionCardAccessGate gate) : IDisposable
    {
        private MotionCardAccessGate? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

/// <summary>四轴反馈与总线检查使用同一优先采集批次，防止普通查询插队。</summary>
public interface IPriorityPressureSampling
{
    IDisposable? EnterPressureSampling();
}
