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

    public int Channel { get; init; }

    public int BitNo => Channel;

    public string Name { get; init; } = "";

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
