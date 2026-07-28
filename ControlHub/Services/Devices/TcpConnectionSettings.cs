using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControlHub.Services.Devices;

public sealed class TcpConnectionSettings : INotifyPropertyChanged
{
    private string _host = "192.168.1.101";
    private int _port = 5025;
    private string _newLine = "\\n";
    private string _manualSendText = "*IDN?";
    private bool _appendNewLine = true;
    private int _commandTimeoutMilliseconds = 5_000;
    private string _frequency = "1KHZ";
    private double _signalLevelVolts = 1.0;
    private string _measurementRange = "AUTO";
    private int _apertureTime = 2;
    private int _cableLengthMeters;
    private bool _averagingEnabled = true;
    private int _averagingCount = 3;
    private bool _comparatorEnabled;
    private bool _bin1Enabled = true;
    private double _bin1LowerPf;
    private double _bin1UpperPf;
    private bool _bin2Enabled = true;
    private double _bin2LowerPf;
    private double _bin2UpperPf;
    private bool _bin3Enabled = true;
    private double _bin3LowerPf;
    private double _bin3UpperPf;
    private bool _lossLimitEnabled;
    private double _lossLower;
    private double _lossUpper = 1;
    private string? _lastSuccessfulConnectionSignature;

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

    public string Frequency
    {
        get => _frequency;
        set => SetField(ref _frequency, value);
    }

    public double SignalLevelVolts
    {
        get => _signalLevelVolts;
        set => SetField(ref _signalLevelVolts, value);
    }

    public string MeasurementRange
    {
        get => _measurementRange;
        set => SetField(ref _measurementRange, value);
    }

    public int ApertureTime
    {
        get => _apertureTime;
        set => SetField(ref _apertureTime, value);
    }

    public int CableLengthMeters
    {
        get => _cableLengthMeters;
        set => SetField(ref _cableLengthMeters, value);
    }

    public bool AveragingEnabled
    {
        get => _averagingEnabled;
        set => SetField(ref _averagingEnabled, value);
    }

    public int AveragingCount
    {
        get => _averagingCount;
        set => SetField(ref _averagingCount, value);
    }

    public bool ComparatorEnabled
    {
        get => _comparatorEnabled;
        set => SetField(ref _comparatorEnabled, value);
    }

    public bool Bin1Enabled
    {
        get => _bin1Enabled;
        set => SetField(ref _bin1Enabled, value);
    }

    public double Bin1LowerPf
    {
        get => _bin1LowerPf;
        set => SetField(ref _bin1LowerPf, value);
    }

    public double Bin1UpperPf
    {
        get => _bin1UpperPf;
        set => SetField(ref _bin1UpperPf, value);
    }

    public bool Bin2Enabled
    {
        get => _bin2Enabled;
        set => SetField(ref _bin2Enabled, value);
    }

    public double Bin2LowerPf
    {
        get => _bin2LowerPf;
        set => SetField(ref _bin2LowerPf, value);
    }

    public double Bin2UpperPf
    {
        get => _bin2UpperPf;
        set => SetField(ref _bin2UpperPf, value);
    }

    public bool Bin3Enabled
    {
        get => _bin3Enabled;
        set => SetField(ref _bin3Enabled, value);
    }

    public double Bin3LowerPf
    {
        get => _bin3LowerPf;
        set => SetField(ref _bin3LowerPf, value);
    }

    public double Bin3UpperPf
    {
        get => _bin3UpperPf;
        set => SetField(ref _bin3UpperPf, value);
    }

    public bool LossLimitEnabled
    {
        get => _lossLimitEnabled;
        set => SetField(ref _lossLimitEnabled, value);
    }

    public double LossLower
    {
        get => _lossLower;
        set => SetField(ref _lossLower, value);
    }

    public double LossUpper
    {
        get => _lossUpper;
        set => SetField(ref _lossUpper, value);
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
