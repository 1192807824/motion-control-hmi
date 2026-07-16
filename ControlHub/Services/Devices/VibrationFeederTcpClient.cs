using System.IO;
using System.Net.Sockets;

namespace ControlHub.Services.Devices;

public sealed class VibrationFeederTcpClient : IDisposable
{
    private const int TimeoutMilliseconds = 2_000;
    private readonly object _syncRoot = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _connectCancellation;
    private CancellationTokenSource? _receiveCancellation;
    private int _writeTimeoutMs = TimeoutMilliseconds;

    public event Action<byte[]>? DataReceived;

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

    public async Task ConnectAsync(VibrationFeederSettings settings, CancellationToken cancellationToken = default)
    {
        Close();

        var host = settings.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("设备 IP 或主机名不能为空。");
        }

        if (settings.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("TCP 端口必须在 1 到 65535 之间。");
        }

        var client = new TcpClient
        {
            NoDelay = true,
            SendTimeout = TimeoutMilliseconds
        };

        var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(TimeoutMilliseconds);

        lock (_syncRoot)
        {
            _connectCancellation = connectCancellation;
        }

        try
        {
            await client.ConnectAsync(host, settings.Port, connectCancellation.Token);

            var stream = client.GetStream();
            var receiveCancellation = new CancellationTokenSource();
            var receiveToken = receiveCancellation.Token;
            var connectionAccepted = false;

            lock (_syncRoot)
            {
                if (ReferenceEquals(_connectCancellation, connectCancellation) &&
                    !connectCancellation.IsCancellationRequested)
                {
                    _connectCancellation = null;
                    _client = client;
                    _stream = stream;
                    _receiveCancellation = receiveCancellation;
                    _writeTimeoutMs = TimeoutMilliseconds;
                    connectionAccepted = true;
                }
            }

            if (!connectionAccepted)
            {
                receiveCancellation.Dispose();
                stream.Dispose();
                client.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("TCP connection attempt was canceled.");
            }

            _ = ReceiveLoopAsync(client, stream, receiveToken);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            bool canceledByClose;
            lock (_syncRoot)
            {
                canceledByClose = !ReferenceEquals(_connectCancellation, connectCancellation);
            }

            if (canceledByClose)
            {
                throw;
            }

            throw new TimeoutException($"连接 {host}:{settings.Port} 超时。");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_connectCancellation, connectCancellation))
                {
                    _connectCancellation = null;
                }
            }

            connectCancellation.Dispose();
        }
    }

    public async Task WriteAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length == 0)
        {
            return;
        }

        NetworkStream stream;
        int timeoutMs;
        lock (_syncRoot)
        {
            stream = _stream ?? throw new InvalidOperationException("TCP 尚未连接。");
            timeoutMs = _writeTimeoutMs;
        }

        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeCancellation.CancelAfter(timeoutMs);

        try
        {
            await stream.WriteAsync(payload, writeCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP 发送超时（{timeoutMs} ms）。");
        }
    }

    public void Close()
    {
        TcpClient? client;
        NetworkStream? stream;
        CancellationTokenSource? connectCancellation;
        CancellationTokenSource? receiveCancellation;

        lock (_syncRoot)
        {
            client = _client;
            stream = _stream;
            connectCancellation = _connectCancellation;
            receiveCancellation = _receiveCancellation;
            _client = null;
            _stream = null;
            _connectCancellation = null;
            _receiveCancellation = null;
        }

        connectCancellation?.Cancel();
        receiveCancellation?.Cancel();
        stream?.Dispose();
        client?.Dispose();
        receiveCancellation?.Dispose();
    }

    public void Dispose()
    {
        Close();
    }

    private async Task ReceiveLoopAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
    {
        Exception? closeReason = null;

        try
        {
            var buffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                DataReceived?.Invoke(buffer[..read]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            closeReason = ex;
        }
        finally
        {
            var notifyClosed = false;
            CancellationTokenSource? receiveCancellation = null;

            lock (_syncRoot)
            {
                if (ReferenceEquals(_client, client))
                {
                    receiveCancellation = _receiveCancellation;
                    _client = null;
                    _stream = null;
                    _receiveCancellation = null;
                    notifyClosed = true;
                }
            }

            stream.Dispose();
            client.Dispose();
            receiveCancellation?.Dispose();

            if (notifyClosed)
            {
                ConnectionClosed?.Invoke(closeReason);
            }
        }
    }
}
