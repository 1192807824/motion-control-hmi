using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ControlHub.Services.Motion;

namespace ControlHub.Models;

public sealed class MotionHomeTuningSettings : INotifyPropertyChanged
{
    private bool _enabled;
    private int _mode = 33;
    private double _lowVelocity = 5;
    private double _highVelocity = 25;
    private double _accelerationMilliseconds = 100;
    private double _decelerationMilliseconds = 100;
    private double _offsetPosition;
    private int _timeoutSeconds = 60;
    private int _sequenceOrder;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public int Mode
    {
        get => _mode;
        set => SetField(ref _mode, value);
    }

    public double LowVelocity
    {
        get => _lowVelocity;
        set => SetField(ref _lowVelocity, value);
    }

    public double HighVelocity
    {
        get => _highVelocity;
        set => SetField(ref _highVelocity, value);
    }

    public double AccelerationMilliseconds
    {
        get => _accelerationMilliseconds;
        set => SetField(ref _accelerationMilliseconds, value);
    }

    public double DecelerationMilliseconds
    {
        get => _decelerationMilliseconds;
        set => SetField(ref _decelerationMilliseconds, value);
    }

    public double OffsetPosition
    {
        get => _offsetPosition;
        set => SetField(ref _offsetPosition, value);
    }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetField(ref _timeoutSeconds, value);
    }

    public int SequenceOrder
    {
        get => _sequenceOrder;
        set => SetField(ref _sequenceOrder, value);
    }

    public void LoadFrom(MotionHomeProfile profile, int timeoutSeconds, int sequenceOrder)
    {
        Enabled = profile.Enabled;
        Mode = profile.Mode;
        LowVelocity = profile.LowVelocity;
        HighVelocity = profile.HighVelocity;
        AccelerationMilliseconds = profile.AccelerationSeconds * 1000;
        DecelerationMilliseconds = profile.DecelerationSeconds * 1000;
        OffsetPosition = profile.OffsetPosition;
        TimeoutSeconds = timeoutSeconds;
        SequenceOrder = sequenceOrder;
    }

    public MotionHomeProfile CreateProfile()
    {
        if (Mode is < ushort.MinValue or > ushort.MaxValue)
        {
            throw new InvalidDataException("回零模式必须是 0 到 65535 之间的整数。");
        }

        if (TimeoutSeconds is < 1 or > 3600)
        {
            throw new InvalidDataException("回零超时必须在 1 到 3600 秒之间。");
        }

        if (SequenceOrder is < 0 or > 64)
        {
            throw new InvalidDataException("回零顺序号必须在 0 到 64 之间；0 表示不参加顺序回零。");
        }

        var profile = new MotionHomeProfile
        {
            Enabled = Enabled,
            Mode = (ushort)Mode,
            LowVelocity = LowVelocity,
            HighVelocity = HighVelocity,
            AccelerationSeconds = AccelerationMilliseconds / 1000,
            DecelerationSeconds = DecelerationMilliseconds / 1000,
            OffsetPosition = OffsetPosition
        };
        profile.Validate(requireEnabled: false);
        return profile;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
