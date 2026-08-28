using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControlHub.Services.Devices;

public sealed class VibrationFeederSettings : INotifyPropertyChanged
{
    private string _host = "192.168.1.100";
    private int _port = 4001;
    private string _newLine = "\\r\\n";
    private string _manualSendText = "";
    private bool _appendNewLine = true;
    private int _lightOnBrightness = 99;
    private int _directionalVibrationFrequency = 25;
    private int _directionalVibrationAmplitude = 65;
    private int _directionalVibrationDurationMilliseconds = 500;
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

    public int LightOnBrightness
    {
        get => _lightOnBrightness;
        set => SetField(ref _lightOnBrightness, Math.Clamp(value, 0, 99));
    }

    public int DirectionalVibrationFrequency
    {
        get => _directionalVibrationFrequency;
        set => SetField(ref _directionalVibrationFrequency, Math.Clamp(value, 1, 999));
    }

    public int DirectionalVibrationAmplitude
    {
        get => _directionalVibrationAmplitude;
        set => SetField(ref _directionalVibrationAmplitude, Math.Clamp(value, 0, 100));
    }

    public int DirectionalVibrationDurationMilliseconds
    {
        get => _directionalVibrationDurationMilliseconds;
        set => SetField(ref _directionalVibrationDurationMilliseconds, Math.Clamp(value, 100, 30000));
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
