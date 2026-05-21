using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ControlHub.Models;

public sealed class AxisStatus : INotifyPropertyChanged
{
    private double _position;
    private double _target;
    private int _speed;
    private bool _servoOn;
    private bool _homed;
    private bool _alarm;
    private bool _positiveLimit;
    private bool _negativeLimit;
    private string _state = "待机";

    public int AxisNo { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "mm";
    public string DisplayPosition => Unit == "deg" ? $"{Position:0.000}°" : $"{Position:0.000}";
    public string DisplayTarget => Unit == "deg" ? $"{Target:0.000}°" : $"{Target:0.000}";
    public string DisplaySpeed => $"{Speed:0.000}";
    public string ServoText => ServoOn ? "使能" : "未使能";
    public string AlarmText => Alarm ? "报警" : "无";
    public Brush ServoBrush => ServoOn ? Brushes.LimeGreen : Brushes.Gray;
    public Brush AlarmBrush => Alarm ? Brushes.Red : Brushes.LimeGreen;
    public Brush PositiveLimitBrush => PositiveLimit ? Brushes.Red : Brushes.LightGray;
    public Brush NegativeLimitBrush => NegativeLimit ? Brushes.Red : Brushes.LightGray;

    public double Position
    {
        get => _position;
        set
        {
            if (SetField(ref _position, value))
            {
                OnPropertyChanged(nameof(DisplayPosition));
            }
        }
    }

    public double Target
    {
        get => _target;
        set
        {
            if (SetField(ref _target, value))
            {
                OnPropertyChanged(nameof(DisplayTarget));
            }
        }
    }

    public int Speed
    {
        get => _speed;
        set
        {
            if (SetField(ref _speed, value))
            {
                OnPropertyChanged(nameof(DisplaySpeed));
            }
        }
    }

    public bool ServoOn
    {
        get => _servoOn;
        set
        {
            if (SetField(ref _servoOn, value))
            {
                OnPropertyChanged(nameof(ServoText));
                OnPropertyChanged(nameof(ServoBrush));
            }
        }
    }

    public bool Homed
    {
        get => _homed;
        set => SetField(ref _homed, value);
    }

    public bool Alarm
    {
        get => _alarm;
        set
        {
            if (SetField(ref _alarm, value))
            {
                OnPropertyChanged(nameof(AlarmText));
                OnPropertyChanged(nameof(AlarmBrush));
            }
        }
    }

    public bool PositiveLimit
    {
        get => _positiveLimit;
        set
        {
            if (SetField(ref _positiveLimit, value))
            {
                OnPropertyChanged(nameof(PositiveLimitBrush));
            }
        }
    }

    public bool NegativeLimit
    {
        get => _negativeLimit;
        set
        {
            if (SetField(ref _negativeLimit, value))
            {
                OnPropertyChanged(nameof(NegativeLimitBrush));
            }
        }
    }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

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
