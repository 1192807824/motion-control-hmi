using System.IO;
using System.IO.Ports;
using System.Text;

namespace ControlHub.Services.Devices;

public sealed class SerialConnectionClient : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly object _syncRoot = new();
    private readonly object _responseSyncRoot = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly List<byte> _receiveBuffer = [];
    private SerialPort? _serialPort;
    private TaskCompletionSource<string>? _pendingResponse;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ResponseReceived;
    public event Action<Exception?>? ConnectionClosed;

    public bool IsConnected
    {
        get
        {
            lock (_syncRoot)
            {
                return _serialPort?.IsOpen == true;
            }
        }
    }

    public void Connect(SerialConnectionSettings settings)
    {
        Close();

        var portName = settings.PortName?.Trim();
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("串口名称不能为空。");
        }
        if (settings.BaudRate <= 0)
        {
            throw new InvalidOperationException("波特率必须大于 0。");
        }
        if (settings.DataBits is < 5 or > 8)
        {
            throw new InvalidOperationException("数据位必须在 5 到 8 之间。");
        }
        if (!Enum.TryParse<Parity>(settings.Parity, true, out var parity))
        {
            throw new InvalidOperationException($"不支持的校验位：{settings.Parity}");
        }
        if (!Enum.TryParse<StopBits>(settings.StopBits, true, out var stopBits) ||
            stopBits == StopBits.None)
        {
            throw new InvalidOperationException($"不支持的停止位：{settings.StopBits}");
        }

        var serialPort = new SerialPort(portName, settings.BaudRate, parity, settings.DataBits, stopBits)
        {
            ReadTimeout = 2_000,
            WriteTimeout = 2_000
        };
        serialPort.DataReceived += SerialPort_DataReceived;

        try
        {
            serialPort.Open();
            lock (_syncRoot)
            {
                _serialPort = serialPort;
            }
        }
        catch
        {
            serialPort.DataReceived -= SerialPort_DataReceived;
            serialPort.Dispose();
            throw;
        }
    }

    public void Write(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        SerialPort serialPort;
        lock (_syncRoot)
        {
            serialPort = _serialPort ?? throw new InvalidOperationException("串口尚未连接。");
        }
        serialPort.Write(payload, 0, payload.Length);
    }

    public Task SendCommandAsync(
        string command,
        string terminator,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        return SendCoreAsync(command, terminator, expectResponse: false, timeoutMilliseconds, cancellationToken);
    }

    public async Task<string> QueryAsync(
        string command,
        string terminator,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        return await SendCoreAsync(command, terminator, expectResponse: true, timeoutMilliseconds, cancellationToken)
            ?? string.Empty;
    }

    public void Close()
    {
        SerialPort? serialPort;
        lock (_syncRoot)
        {
            serialPort = _serialPort;
            _serialPort = null;
        }

        TaskCompletionSource<string>? pendingResponse;
        lock (_responseSyncRoot)
        {
            pendingResponse = _pendingResponse;
            _pendingResponse = null;
            _receiveBuffer.Clear();
        }
        pendingResponse?.TrySetException(new IOException("串口连接已关闭。"));

        if (serialPort is null)
        {
            return;
        }

        serialPort.DataReceived -= SerialPort_DataReceived;
        try
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }
        }
        finally
        {
            serialPort.Dispose();
        }
    }

    public void Dispose()
    {
        Close();
        _commandLock.Dispose();
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort serialPort)
        {
            return;
        }

        try
        {
            var bytesToRead = serialPort.BytesToRead;
            if (bytesToRead <= 0)
            {
                return;
            }

            var payload = new byte[bytesToRead];
            var read = serialPort.Read(payload, 0, payload.Length);
            if (read > 0)
            {
                var received = read == payload.Length ? payload : payload[..read];
                DataReceived?.Invoke(received);
                ProcessReceivedBytes(received);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Close();
            ConnectionClosed?.Invoke(ex);
        }
    }

    private async Task<string?> SendCoreAsync(
        string command,
        string terminator,
        bool expectResponse,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("SCPI命令不能为空。");
        }
        if (string.IsNullOrEmpty(terminator))
        {
            throw new InvalidOperationException("SCPI命令结束符不能为空。");
        }
        if (timeoutMilliseconds is < 500 or > 60_000)
        {
            throw new InvalidOperationException("命令超时必须在500到60000毫秒之间。");
        }

        await _commandLock.WaitAsync(cancellationToken);
        TaskCompletionSource<string>? responseSource = null;
        try
        {
            SerialPort serialPort;
            lock (_syncRoot)
            {
                serialPort = _serialPort ?? throw new InvalidOperationException("串口尚未连接。");
            }

            if (expectResponse)
            {
                responseSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_responseSyncRoot)
                {
                    _receiveBuffer.Clear();
                    _pendingResponse = responseSource;
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            var payload = Encoding.ASCII.GetBytes(command.TrimEnd('\r', '\n') + terminator);
            try
            {
                await serialPort.BaseStream.WriteAsync(payload, timeout.Token);
                await serialPort.BaseStream.FlushAsync(timeout.Token);
                if (!expectResponse)
                {
                    return null;
                }

                return await responseSource!.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"串口命令超时：{command}");
            }
        }
        finally
        {
            if (responseSource is not null)
            {
                lock (_responseSyncRoot)
                {
                    if (ReferenceEquals(_pendingResponse, responseSource))
                    {
                        _pendingResponse = null;
                    }
                }
            }
            _commandLock.Release();
        }
    }

    private void ProcessReceivedBytes(byte[] payload)
    {
        var completedLines = new List<(string Line, TaskCompletionSource<string>? ResponseSource)>();
        TaskCompletionSource<string>? overflowSource = null;
        lock (_responseSyncRoot)
        {
            _receiveBuffer.AddRange(payload);
            if (_receiveBuffer.Count > MaximumResponseBytes)
            {
                _receiveBuffer.Clear();
                overflowSource = _pendingResponse;
                _pendingResponse = null;
            }
            else
            {
                while (TryTakeLine(_receiveBuffer, out var line))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    var responseSource = _pendingResponse;
                    _pendingResponse = null;
                    completedLines.Add((line, responseSource));
                }
            }
        }

        overflowSource?.TrySetException(new InvalidDataException("串口响应超过64KB，已停止接收。"));
        foreach (var (line, responseSource) in completedLines)
        {
            ResponseReceived?.Invoke(line);
            responseSource?.TrySetResult(line);
        }
    }

    private static bool TryTakeLine(List<byte> buffer, out string line)
    {
        var delimiterIndex = buffer.FindIndex(value => value is (byte)'\r' or (byte)'\n');
        if (delimiterIndex < 0)
        {
            line = string.Empty;
            return false;
        }

        var lineBytes = buffer.GetRange(0, delimiterIndex).ToArray();
        var removeCount = delimiterIndex + 1;
        while (removeCount < buffer.Count && buffer[removeCount] is (byte)'\r' or (byte)'\n')
        {
            removeCount++;
        }
        buffer.RemoveRange(0, removeCount);
        line = Encoding.ASCII.GetString(lineBytes).TrimEnd();
        return true;
    }
}
