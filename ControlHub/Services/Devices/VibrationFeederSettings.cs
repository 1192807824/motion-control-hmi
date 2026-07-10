using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControlHub.Services.Devices;

public sealed class VibrationFeederSettings : INotifyPropertyChanged
{
    private string _portName = "COM1";
    private int _baudRate = 9600;
    private int _dataBits = 8;
    private string _parity = "None";
    private string _stopBits = "One";
    private string _handshake = "None";
    private string _newLine = "\\r\\n";
    private int _readTimeoutMs = 500;
    private int _writeTimeoutMs = 500;
    private string _encodingName = "ASCII";
    private string _sendFormat = "ASCII";
    private string _receiveFormat = "ASCII";
    private string _manualSendText = "";
    private bool _appendNewLine = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PortName
    {
        get => _portName;
        set => SetField(ref _portName, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetField(ref _baudRate, value);
    }

    public int DataBits
    {
        get => _dataBits;
        set => SetField(ref _dataBits, value);
    }

    public string Parity
    {
        get => _parity;
        set => SetField(ref _parity, value);
    }

    public string StopBits
    {
        get => _stopBits;
        set => SetField(ref _stopBits, value);
    }

    public string Handshake
    {
        get => _handshake;
        set => SetField(ref _handshake, value);
    }

    public string NewLine
    {
        get => _newLine;
        set => SetField(ref _newLine, value);
    }

    public int ReadTimeoutMs
    {
        get => _readTimeoutMs;
        set => SetField(ref _readTimeoutMs, value);
    }

    public int WriteTimeoutMs
    {
        get => _writeTimeoutMs;
        set => SetField(ref _writeTimeoutMs, value);
    }

    public string EncodingName
    {
        get => _encodingName;
        set => SetField(ref _encodingName, value);
    }

    public string SendFormat
    {
        get => _sendFormat;
        set => SetField(ref _sendFormat, value);
    }

    public string ReceiveFormat
    {
        get => _receiveFormat;
        set => SetField(ref _receiveFormat, value);
    }

    public string ManualSendText
    {
        get => _manualSendText;
        set => SetField(ref _manualSendText, value);
    }

    public bool AppendNewLine
    {
        get => _appendNewLine;
        set => SetField(ref _appendNewLine, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
