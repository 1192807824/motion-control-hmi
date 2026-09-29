using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace ControlHub.Services.Motion;

/// <summary>Observes production without issuing hardware commands or waiting on file I/O.</summary>
internal sealed class ProductionDiagnosticSession : IDisposable
{
    private readonly BlockingCollection<string> _lines = new(4096);
    private readonly ConcurrentDictionary<string, string> _phases = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<string> _captureUiState;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly System.Threading.Timer _timer;
    private readonly Task _writer;
    private long _lastUiBeat;
    private int _uiBeatPending;
    private int _disposed;
    private string _uiState = "not sampled yet";

    public string LogPath { get; }
    public string? WriteError { get; private set; }

    public ProductionDiagnosticSession(Dispatcher dispatcher, Func<string> captureUiState, string? directory = null)
    {
        _dispatcher = dispatcher;
        _captureUiState = captureUiState;
        LogPath = Path.Combine(directory ?? Path.Combine(AppContext.BaseDirectory, "logs", "production"),
            $"production-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
        // One background writer: production never opens, flushes or waits for a log file.
        _writer = Task.Factory.StartNew(WriteLines, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Write("SESSION", $"START assembly={typeof(ProductionDiagnosticSession).Assembly.Location} " +
            $"build_mvid={typeof(ProductionDiagnosticSession).Module.ModuleVersionId}");
        _timer = new System.Threading.Timer(Heartbeat, null, 0, 500);
    }

    public IDisposable Begin(string area, string detail)
    {
        var phase = $"{detail} since_ms={_elapsed.ElapsedMilliseconds}";
        _phases[area] = phase;
        Write(area, "BEGIN " + detail);
        return new Step(this, area, phase, detail);
    }

    public void Track(string name, Task task)
    {
        Write(name, "TASK " + task.Status);
        _ = task.ContinueWith(completed =>
        {
            Write(name, "TASK " + completed.Status +
                (completed.Exception is { } error ? " " + error.Flatten() : ""));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Write(string area, string detail)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _lines.TryAdd($"{DateTime.Now:O} elapsed_ms={_elapsed.ElapsedMilliseconds} " +
                $"thread={Environment.CurrentManagedThreadId} [{area}] {detail.Replace('\r', ' ').Replace('\n', ' ')}");
        }
        catch (InvalidOperationException) { /* Session ended concurrently. */ }
    }

    private void Heartbeat(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var gap = _elapsed.ElapsedMilliseconds - Interlocked.Read(ref _lastUiBeat);
        Write("HEARTBEAT", $"ui_gap_ms={gap} phases={string.Join(" | ", _phases.Select(p => p.Key + ":" + p.Value))} " +
            $"cached_ui={Volatile.Read(ref _uiState)}");
        if (Interlocked.CompareExchange(ref _uiBeatPending, 1, 0) != 0) return;
        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        Volatile.Write(ref _uiState, _captureUiState());
                        Interlocked.Exchange(ref _lastUiBeat, _elapsed.ElapsedMilliseconds);
                    }
                }
                catch (Exception error) { Write("SNAPSHOT", error.Message); }
                finally { Interlocked.Exchange(ref _uiBeatPending, 0); }
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _uiBeatPending, 0); }
    }

    private void WriteLines()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            using var writer = new StreamWriter(LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            long characters = 0;
            foreach (var line in _lines.GetConsumingEnumerable())
            {
                writer.WriteLine(line);
                characters += line.Length;
                if (characters < 8_000_000) continue;
                writer.WriteLine("LOG LIMIT reached; stop recording this session.");
                break;
            }
        }
        catch (Exception error) { WriteError = error.Message; }
    }

    public void Dispose()
    {
        Write("SESSION", "END");
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        _lines.CompleteAdding();
    }

    internal Task Completion => _writer;

    private sealed class Step(ProductionDiagnosticSession owner, string area, string phase, string detail) : IDisposable
    {
        private readonly Stopwatch _duration = Stopwatch.StartNew();
        public void Dispose()
        {
            ((ICollection<KeyValuePair<string, string>>)owner._phases).Remove(new(area, phase));
            owner.Write(area, $"END duration_ms={_duration.ElapsedMilliseconds} {detail}");
        }
    }
}
