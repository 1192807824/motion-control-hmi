using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ControlHub.Models;

public enum IoPointKind
{
    DigitalInput,
    DigitalOutput,
    AnalogInput,
    AnalogOutput
}

public sealed class IoPoint : INotifyPropertyChanged
{
    private bool _isOn;
    private double _value;
    private string _name = "";

    public int Channel { get; init; }

    public int HardwareBitNo { get; init; } = -1;

    public int BitNo => HardwareBitNo >= 0 ? HardwareBitNo : Channel;

    public string DefaultName { get; init; } = "";

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string NameEditToolTip => $"{DefaultName} · 双击修改名称";

    public IoPointKind Kind { get; init; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            _isOn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LampBrush));
            OnPropertyChanged(nameof(DigitalOutputText));
        }
    }

    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) < 0.000001)
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayValue));
        }
    }

    public string DisplayValue => $"{Value:0.000}";

    public string DigitalOutputText => IsOn ? "ON" : "OFF";

    public Brush LampBrush => IsOn ? Brushes.LimeGreen : Brushes.DimGray;

    public Visibility DigitalInputVisibility => Kind == IoPointKind.DigitalInput ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DigitalOutputVisibility => Kind == IoPointKind.DigitalOutput ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AnalogInputVisibility => Kind == IoPointKind.AnalogInput ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AnalogOutputVisibility => Kind == IoPointKind.AnalogOutput ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
