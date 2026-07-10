using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ControlHub.Models;

public sealed class AxisStatus : INotifyPropertyChanged
{
    private double _position;
    private double _target;
    private double _jogSpeed = 25;
    private double _jogDistance = 10;
    private double _speed;
    private bool _servoOn;
    private bool _homed;
    private bool _alarm;
    private bool _positiveLimit;
    private bool _negativeLimit;
    private bool _isAvailable;
    private bool _isMoving;
    private string _name = "";
    private string _state = "未连接";

    public int AxisNo { get; init; }
    public int HardwareAxisNo => AxisNo - 1;
    public string Name
    {
        get => _name;
        set => SetField(ref _name, NormalizeName(value));
    }
    public string Unit { get; init; } = "mm";
    public string DisplayPosition => $"{Position:0.000}";
    public string DisplayTarget => $"{Target:0.000}";
    public string DisplaySpeed => $"{Speed:0.000}";
    public string ServoText => !IsAvailable ? "未连接" : ServoOn ? "已使能" : "未使能";
    public string AlarmText => Alarm ? "报警" : "无";
    public bool CanServoOn => IsAvailable && !ServoOn;
    public bool CanServoOff => IsAvailable && ServoOn;
    public bool CanMove => IsAvailable && ServoOn && !Alarm;
    public bool CanStop => IsAvailable && IsMoving;
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

    public double JogSpeed
    {
        get => _jogSpeed;
        set => SetField(ref _jogSpeed, Math.Max(0, value));
    }

    public double JogDistance
    {
        get => _jogDistance;
        set => SetField(ref _jogDistance, Math.Max(0, value));
    }

    public double Speed
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
                OnPropertyChanged(nameof(CanServoOn));
                OnPropertyChanged(nameof(CanServoOff));
                OnPropertyChanged(nameof(CanMove));
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
                OnPropertyChanged(nameof(CanMove));
            }
        }
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetField(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(ServoText));
                OnPropertyChanged(nameof(CanServoOn));
                OnPropertyChanged(nameof(CanServoOff));
                OnPropertyChanged(nameof(CanMove));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool IsMoving
    {
        get => _isMoving;
        set
        {
            if (SetField(ref _isMoving, value))
            {
                OnPropertyChanged(nameof(CanStop));
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

    private static string NormalizeName(string? name)
    {
        return name?.Trim() ?? "";
    }
}
