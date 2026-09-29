using System.Collections.Concurrent;
using ControlHub.Models;

namespace ControlHub.Services.Persistence;

// Alarm producers never wait for disk I/O: recording must not delay emergency stops or safe returns.
public static class AlarmHistory
{
    private static readonly ConcurrentQueue<AlarmInfo> Pending = new();
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private static AlarmHistoryStore? _store;
    private static long _revision;
    public static long Revision => Interlocked.Read(ref _revision);
    public static string? LastError { get; private set; }
    public static void Initialize(AlarmHistoryStore? store = null) => _store ??= store ?? new AlarmHistoryStore();

    public static void Record(string source, string code, string message, string level = "报警")
    {
        if (_store is null) return; // Detached UI tests do not write to the machine's history.
        Pending.Enqueue(new AlarmInfo { Source = source, Code = code, Message = message, Level = level });
        _ = Task.Run(FlushAsync);
    }

    public static async Task FlushAsync()
    {
        await Writer.WaitAsync().ConfigureAwait(false);
        try { FlushCore(); }
        finally { Writer.Release(); }
    }

    private static void FlushCore()
    {
        if (_store is null) return;
        try
        {
            while (Pending.TryPeek(out var alarm))
            {
                _store.Append(alarm);
                Pending.TryDequeue(out _);
                Interlocked.Increment(ref _revision);
            }
            LastError = null;
        }
        catch (Exception exception) { LastError = $"报警保存失败（尚有{Pending.Count}条待保存）：{exception.Message}"; }
    }

    public static async Task<AlarmQueryResult> QueryAsync(DateTime? from, DateTime? through, int page = 0)
    {
        Initialize();
        return await Task.Run(async () =>
        {
            await FlushAsync();
            return _store!.Query(from, through, page);
        });
    }

    public static async Task<int> ClearAllAsync()
    {
        Initialize();
        return await Task.Run(async () =>
        {
            await Writer.WaitAsync();
            try
            {
                FlushCore();
                if (LastError is not null) throw new InvalidOperationException(LastError);
                var count = _store!.ClearAll();
                Interlocked.Increment(ref _revision);
                return count;
            }
            finally { Writer.Release(); }
        });
    }
}
