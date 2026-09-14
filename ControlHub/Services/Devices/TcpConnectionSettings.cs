using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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
    private int _stabilityMaximumTestCount = 10;
    private int _stabilitySampleCount = 2;
    private double _stabilityCapacitancePercent = 1;
    private double _stabilityDissipationTolerance = 0.005;
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

    public int StabilityMaximumTestCount
    {
        get => _stabilityMaximumTestCount;
        set => SetField(ref _stabilityMaximumTestCount, value);
    }

    public int StabilitySampleCount
    {
        get => _stabilitySampleCount;
        set => SetField(ref _stabilitySampleCount, value);
    }

    public double StabilityCapacitancePercent
    {
        get => _stabilityCapacitancePercent;
        set => SetField(ref _stabilityCapacitancePercent, value);
    }

    public double StabilityDissipationTolerance
    {
        get => _stabilityDissipationTolerance;
        set => SetField(ref _stabilityDissipationTolerance, value);
    }

    public bool Bin1Enabled
    {
        get => _bin1Enabled;
        set => SetField(ref _bin1Enabled, value);
    }

    // 保留历史配置/配方的pF存储字段，界面统一通过nF属性读写，避免旧范围被放大1000倍。
    [JsonIgnore]
    public double Bin1LowerNf { get => Bin1LowerPf / 1000; set => Bin1LowerPf = value * 1000; }

    [JsonIgnore]
    public double Bin1UpperNf { get => Bin1UpperPf / 1000; set => Bin1UpperPf = value * 1000; }

    [JsonIgnore]
    public double Bin2LowerNf { get => Bin2LowerPf / 1000; set => Bin2LowerPf = value * 1000; }

    [JsonIgnore]
    public double Bin2UpperNf { get => Bin2UpperPf / 1000; set => Bin2UpperPf = value * 1000; }

    [JsonIgnore]
    public double Bin3LowerNf { get => Bin3LowerPf / 1000; set => Bin3LowerPf = value * 1000; }

    [JsonIgnore]
    public double Bin3UpperNf { get => Bin3UpperPf / 1000; set => Bin3UpperPf = value * 1000; }

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
        var nfPropertyName = propertyName switch
        {
            nameof(Bin1LowerPf) => nameof(Bin1LowerNf),
            nameof(Bin1UpperPf) => nameof(Bin1UpperNf),
            nameof(Bin2LowerPf) => nameof(Bin2LowerNf),
            nameof(Bin2UpperPf) => nameof(Bin2UpperNf),
            nameof(Bin3LowerPf) => nameof(Bin3LowerNf),
            nameof(Bin3UpperPf) => nameof(Bin3UpperNf),
            _ => null
        };
        if (nfPropertyName is not null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nfPropertyName));
    }
}
