using System.IO;
using System.IO.Ports;
using System.Text;

namespace ControlHub.Services.Devices;

public sealed class VibrationFeederSerialClient : IDisposable
{
    private SerialPort? _serialPort;

    static VibrationFeederSerialClient()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public event Action<byte[]>? DataReceived;

    public bool IsOpen => _serialPort?.IsOpen == true;

    public string? PortName => _serialPort?.PortName;

    public void Open(VibrationFeederSettings settings)
    {
        Close();

        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            throw new InvalidOperationException("Port name is required.");
        }

        var port = new SerialPort(
            settings.PortName.Trim(),
            settings.BaudRate,
            ParseEnum(settings.Parity, Parity.None),
            settings.DataBits,
            ParseEnum(settings.StopBits, StopBits.One))
        {
            Handshake = ParseEnum(settings.Handshake, Handshake.None),
            ReadTimeout = Math.Max(50, settings.ReadTimeoutMs),
            WriteTimeout = Math.Max(50, settings.WriteTimeoutMs),
            Encoding = ResolveEncoding(settings.EncodingName)
        };

        var newLine = DecodeNewLine(settings.NewLine);
        if (!string.IsNullOrEmpty(newLine))
        {
            port.NewLine = newLine;
        }

        port.DataReceived += SerialPort_DataReceived;
        port.Open();
        _serialPort = port;
    }

    public void Close()
    {
        if (_serialPort is null)
        {
            return;
        }

        try
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.DataReceived -= SerialPort_DataReceived;
                _serialPort.Close();
            }
        }
        finally
        {
            _serialPort.Dispose();
            _serialPort = null;
        }
    }

    public void Write(byte[] payload)
    {
        if (_serialPort is not { IsOpen: true } port)
        {
            throw new InvalidOperationException("Serial port is not open.");
        }

        if (payload.Length == 0)
        {
            return;
        }

        port.Write(payload, 0, payload.Length);
    }

    public void Dispose()
    {
        Close();
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static Encoding ResolveEncoding(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return Encoding.ASCII;
        }

        try
        {
            return Encoding.GetEncoding(encodingName.Trim());
        }
        catch (ArgumentException)
        {
            return Encoding.ASCII;
        }
        catch (NotSupportedException)
        {
            return Encoding.ASCII;
        }
    }

    private static string DecodeNewLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\r", "\r", StringComparison.OrdinalIgnoreCase)
            .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\t", "\t", StringComparison.OrdinalIgnoreCase);
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port)
        {
            return;
        }

        try
        {
            var bytesToRead = port.BytesToRead;
            if (bytesToRead <= 0)
            {
                return;
            }

            var buffer = new byte[bytesToRead];
            var read = port.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length)
            {
                Array.Resize(ref buffer, read);
            }

            DataReceived?.Invoke(buffer);
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
