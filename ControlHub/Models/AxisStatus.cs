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
    private bool _statusReadHealthy;
    private bool _isMoving;
    private bool _homeConfigured;
    private string _name = "";
    private string _state = "未连接";
    private string _homeConfigurationSummary = "未配置回零参数";

    public int AxisNo { get; init; }
    public int HardwareAxisNo => AxisNo;
    public string Name
    {
        get => _name;
        set => SetField(ref _name, NormalizeName(value));
    }
    public string Unit { get; init; } = "mm";
    public string DisplayPosition => $"{Position:0.000}";
    public string DisplayTarget => $"{Target:0.000}";
    public string DisplaySpeed => $"{Speed:0.000}";
    public string ServoText => !IsAvailable ? "未连接" : !StatusReadHealthy ? "状态未知" : ServoOn ? "已使能" : "未使能";
    public string AlarmText => !StatusReadHealthy ? "未知" : Alarm ? "报警" : "无";
    public string HomedText => !StatusReadHealthy ? "未知" : Homed ? "已回零" : "未回零";
    public string PositiveLimitText => !StatusReadHealthy ? "未知" : PositiveLimit ? "已触发" : "未触发";
    public string NegativeLimitText => !StatusReadHealthy ? "未知" : NegativeLimit ? "已触发" : "未触发";
    public string CardStateText => !IsAvailable
        ? "未连接"
        : !StatusReadHealthy
            ? "通讯异常"
        : Alarm
            ? "报警"
            : PositiveLimit
                ? "正限位"
                : NegativeLimit
                    ? "负限位"
                    : IsMoving
                        ? "运行中"
                        : !ServoOn
                            ? "未使能"
                            : Homed
                                ? "正常"
                                : "未回零";
    public bool CanServoOn => IsAvailable && StatusReadHealthy && !ServoOn;
    public bool CanServoOff => IsAvailable && StatusReadHealthy && ServoOn && !IsMoving;
    public bool CanMove => IsAvailable && StatusReadHealthy && ServoOn && !Alarm && !IsMoving;
    public bool CanJogHoldInput => IsAvailable && StatusReadHealthy && ServoOn && !Alarm;
    public bool CanStop => IsAvailable && (IsMoving || !StatusReadHealthy);
    public bool CanEditMotionParameters => IsAvailable && StatusReadHealthy && !IsMoving;
    public bool CanHome => CanMove && HomeConfigured;
    public Brush ServoBrush => !StatusReadHealthy ? Brushes.Gray : ServoOn ? Brushes.LimeGreen : Brushes.Gray;
    public Brush AlarmBrush => !StatusReadHealthy ? Brushes.Gray : Alarm ? Brushes.Red : Brushes.LimeGreen;
    public Brush PositiveLimitBrush => !StatusReadHealthy ? Brushes.Gray : PositiveLimit ? Brushes.Red : Brushes.LightGray;
    public Brush NegativeLimitBrush => !StatusReadHealthy ? Brushes.Gray : NegativeLimit ? Brushes.Red : Brushes.LightGray;
    public Brush HomedBrush => !StatusReadHealthy ? Brushes.Gray : Homed ? Brushes.LimeGreen : Brushes.Gray;
    public Brush StatusBrush => !IsAvailable || !StatusReadHealthy ? Brushes.Gray : Alarm ? Brushes.Red : ServoOn ? Brushes.LimeGreen : Brushes.DarkGray;

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
        set => SetField(ref _jogDistance, value);
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
                OnPropertyChanged(nameof(CanJogHoldInput));
                OnPropertyChanged(nameof(CanHome));
                OnPropertyChanged(nameof(ServoBrush));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(CardStateText));
            }
        }
    }

    public bool Homed
    {
        get => _homed;
        set
        {
            if (SetField(ref _homed, value))
            {
                OnPropertyChanged(nameof(HomedText));
                OnPropertyChanged(nameof(HomedBrush));
                OnPropertyChanged(nameof(CardStateText));
            }
        }
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
                OnPropertyChanged(nameof(CanJogHoldInput));
                OnPropertyChanged(nameof(CanHome));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(CardStateText));
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
                OnPropertyChanged(nameof(CanJogHoldInput));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanEditMotionParameters));
                OnPropertyChanged(nameof(CanHome));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(CardStateText));
            }
        }
    }

    public bool StatusReadHealthy
    {
        get => _statusReadHealthy;
        set
        {
            if (SetField(ref _statusReadHealthy, value))
            {
                OnPropertyChanged(nameof(ServoText));
                OnPropertyChanged(nameof(AlarmText));
                OnPropertyChanged(nameof(HomedText));
                OnPropertyChanged(nameof(PositiveLimitText));
                OnPropertyChanged(nameof(NegativeLimitText));
                OnPropertyChanged(nameof(CardStateText));
                OnPropertyChanged(nameof(CanServoOn));
                OnPropertyChanged(nameof(CanServoOff));
                OnPropertyChanged(nameof(CanMove));
                OnPropertyChanged(nameof(CanJogHoldInput));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanEditMotionParameters));
                OnPropertyChanged(nameof(CanHome));
                OnPropertyChanged(nameof(ServoBrush));
                OnPropertyChanged(nameof(AlarmBrush));
                OnPropertyChanged(nameof(PositiveLimitBrush));
                OnPropertyChanged(nameof(NegativeLimitBrush));
                OnPropertyChanged(nameof(HomedBrush));
                OnPropertyChanged(nameof(StatusBrush));
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
                OnPropertyChanged(nameof(CanMove));
                OnPropertyChanged(nameof(CanServoOff));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanEditMotionParameters));
                OnPropertyChanged(nameof(CanHome));
                OnPropertyChanged(nameof(CardStateText));
            }
        }
    }

    public bool HomeConfigured
    {
        get => _homeConfigured;
        set
        {
            if (SetField(ref _homeConfigured, value))
            {
                OnPropertyChanged(nameof(CanHome));
            }
        }
    }

    public string HomeConfigurationSummary
    {
        get => _homeConfigurationSummary;
        set => SetField(ref _homeConfigurationSummary, value?.Trim() ?? "未配置回零参数");
    }

    public bool PositiveLimit
    {
        get => _positiveLimit;
        set
        {
            if (SetField(ref _positiveLimit, value))
            {
                OnPropertyChanged(nameof(PositiveLimitBrush));
                OnPropertyChanged(nameof(PositiveLimitText));
                OnPropertyChanged(nameof(CardStateText));
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
                OnPropertyChanged(nameof(NegativeLimitText));
                OnPropertyChanged(nameof(CardStateText));
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
