using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControlHub.Services.Devices;

public sealed class VibrationFeederSettings : INotifyPropertyChanged
{
    private string _host = "192.168.1.100";
    private int _port = 4001;
    private int _connectTimeoutMs = 3000;
    private string _newLine = "\\r\\n";
    private int _writeTimeoutMs = 500;
    private string _sendFormat = "ASCII";
    private string _receiveFormat = "ASCII";
    private string _manualSendText = "";
    private bool _appendNewLine = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    public int ConnectTimeoutMs
    {
        get => _connectTimeoutMs;
        set => SetField(ref _connectTimeoutMs, value);
    }

    public string NewLine
    {
        get => _newLine;
        set => SetField(ref _newLine, value);
    }

    public int WriteTimeoutMs
    {
        get => _writeTimeoutMs;
        set => SetField(ref _writeTimeoutMs, value);
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
