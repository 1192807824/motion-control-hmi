using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControlHub.Services.Devices;

public sealed class SerialConnectionSettings : INotifyPropertyChanged
{
    private string _portName = "COM1";
    private int _baudRate = 9600;
    private int _dataBits = 8;
    private string _parity = "None";
    private string _stopBits = "One";
    private string _newLine = "\\r\\n";
    private string _manualSendText = "*IDN?";
    private bool _appendNewLine = true;
    private int _commandTimeoutMilliseconds = 10_000;
    private string _measurementMode = "R";
    private double _appliedVoltageVolts = 100;
    private string _measurementSpeed = "FAST2";
    private string _measurementRange = "AUTO";
    private string _averageMode = "OFF";
    private int _averageCount = 5;
    private bool _interlockEnabled = true;
    private bool _currentLimitEnabled = true;
    private string _currentLimit = "1.8mA";
    private string? _lastSuccessfulConnectionSignature;

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

    public string NewLine
    {
        get => _newLine;
        set => SetField(ref _newLine, value);
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

    public int CommandTimeoutMilliseconds
    {
        get => _commandTimeoutMilliseconds;
        set => SetField(ref _commandTimeoutMilliseconds, value);
    }

    public string MeasurementMode
    {
        get => _measurementMode;
        set => SetField(ref _measurementMode, value);
    }

    public double AppliedVoltageVolts
    {
        get => _appliedVoltageVolts;
        set => SetField(ref _appliedVoltageVolts, value);
    }

    public string MeasurementSpeed
    {
        get => _measurementSpeed;
        set => SetField(ref _measurementSpeed, value);
    }

    public string MeasurementRange
    {
        get => _measurementRange;
        set => SetField(ref _measurementRange, value);
    }

    public string AverageMode
    {
        get => _averageMode;
        set => SetField(ref _averageMode, value);
    }

    public int AverageCount
    {
        get => _averageCount;
        set => SetField(ref _averageCount, value);
    }

    public bool InterlockEnabled
    {
        get => _interlockEnabled;
        set => SetField(ref _interlockEnabled, value);
    }

    public bool CurrentLimitEnabled
    {
        get => _currentLimitEnabled;
        set => SetField(ref _currentLimitEnabled, value);
    }

    public string CurrentLimit
    {
        get => _currentLimit;
        set => SetField(ref _currentLimit, value);
    }

    public string? LastSuccessfulConnectionSignature
    {
        get => _lastSuccessfulConnectionSignature;
        set => SetField(ref _lastSuccessfulConnectionSignature, value);
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
