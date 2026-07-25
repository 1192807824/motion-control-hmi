using System.IO;
using System.IO.Ports;

namespace ControlHub.Services.Devices;

public sealed class SerialConnectionClient : IDisposable
{
    private readonly object _syncRoot = new();
    private SerialPort? _serialPort;

    public event Action<byte[]>? DataReceived;
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

    public void Close()
    {
        SerialPort? serialPort;
        lock (_syncRoot)
        {
            serialPort = _serialPort;
            _serialPort = null;
        }

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

    public void Dispose() => Close();

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
                DataReceived?.Invoke(read == payload.Length ? payload : payload[..read]);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Close();
            ConnectionClosed?.Invoke(ex);
        }
    }
}
