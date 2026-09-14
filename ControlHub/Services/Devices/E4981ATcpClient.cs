using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ControlHub.Services.Devices;

public sealed class E4981ATcpClient : IDisposable
{
    private const int ConnectTimeoutMilliseconds = 3_000;
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly List<byte> _receiveBuffer = [];
    private TcpClient? _client;
    private NetworkStream? _stream;

    public event Action<Exception?>? ConnectionClosed;

    public bool IsConnected
    {
        get
        {
            lock (_syncRoot)
            {
                return _client?.Connected == true && _stream is not null;
            }
        }
    }

    public async Task ConnectAsync(TcpConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        Close();
        var host = settings.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("E4981A IP地址不能为空。");
        }
        if (settings.Port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("TCP端口必须在1到65535之间。");
        }

        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeoutMilliseconds);
        try
        {
            await client.ConnectAsync(host, settings.Port, timeout.Token);
            lock (_syncRoot)
            {
                _client = client;
                _stream = client.GetStream();
                _receiveBuffer.Clear();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"连接E4981A {host}:{settings.Port}超时。");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task SendCommandAsync(string command, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        return SendCoreAsync(command, expectResponse: false, timeoutMilliseconds, cancellationToken);
    }

    public async Task<string> QueryAsync(string command, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        return await SendCoreAsync(command, expectResponse: true, timeoutMilliseconds, cancellationToken) ?? string.Empty;
    }

    public void Close()
    {
        TcpClient? client;
        NetworkStream? stream;
        lock (_syncRoot)
        {
            client = _client;
            stream = _stream;
            _client = null;
            _stream = null;
            _receiveBuffer.Clear();
        }
        stream?.Dispose();
        client?.Dispose();
    }

    public void Dispose()
    {
        Close();
        _commandLock.Dispose();
    }

    private async Task<string?> SendCoreAsync(
        string command,
        bool expectResponse,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("SCPI命令不能为空。");
        }
        if (timeoutMilliseconds is < 500 or > 60_000)
        {
            throw new InvalidOperationException("命令超时必须在500到60000毫秒之间。");
        }

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            NetworkStream stream;
            lock (_syncRoot)
            {
                stream = _stream ?? throw new InvalidOperationException("E4981A尚未连接。");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            var payload = Encoding.ASCII.GetBytes(command.TrimEnd('\r', '\n') + "\n");
            try
            {
                await stream.WriteAsync(payload, timeout.Token);
                await stream.FlushAsync(timeout.Token);
                return expectResponse ? await ReadLineAsync(stream, timeout.Token) : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"E4981A命令超时：{command}");
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Close();
                ConnectionClosed?.Invoke(ex);
                throw;
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var readBuffer = new byte[1024];
        while (true)
        {
            var newlineIndex = _receiveBuffer.IndexOf((byte)'\n');
            if (newlineIndex >= 0)
            {
                var line = _receiveBuffer.GetRange(0, newlineIndex).ToArray();
                _receiveBuffer.RemoveRange(0, newlineIndex + 1);
                return Encoding.ASCII.GetString(line).TrimEnd('\r');
            }
            if (_receiveBuffer.Count > MaximumResponseBytes)
            {
                _receiveBuffer.Clear();
                throw new InvalidDataException("E4981A响应超过64KB，已停止接收。");
            }

            var read = await stream.ReadAsync(readBuffer, cancellationToken);
            if (read == 0)
            {
                throw new IOException("E4981A已关闭TCP连接。");
            }
            _receiveBuffer.AddRange(readBuffer.AsSpan(0, read).ToArray());
        }
    }
}
