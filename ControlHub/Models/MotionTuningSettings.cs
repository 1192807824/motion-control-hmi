using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ControlHub.Services.Motion;

namespace ControlHub.Models;

public sealed class MotionTuningSettings : INotifyPropertyChanged
{
    private double _startVelocity;
    private double _stopVelocity;
    private double _accelerationMilliseconds = 100;
    private double _decelerationMilliseconds = 100;
    private double _sTimeMilliseconds;
    private double _decelerationStopMilliseconds = 100;
    private bool _waitForCompletion = true;
    private int _completionTimeoutMilliseconds = 5000;
    private double _completionTolerance = 0.01;
    private bool _absolutePositionMode;

    public double StartVelocity
    {
        get => _startVelocity;
        set => SetField(ref _startVelocity, value);
    }

    public double StopVelocity
    {
        get => _stopVelocity;
        set => SetField(ref _stopVelocity, value);
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

    public double STimeMilliseconds
    {
        get => _sTimeMilliseconds;
        set => SetField(ref _sTimeMilliseconds, value);
    }

    public double DecelerationStopMilliseconds
    {
        get => _decelerationStopMilliseconds;
        set => SetField(ref _decelerationStopMilliseconds, value);
    }

    public bool WaitForCompletion
    {
        get => _waitForCompletion;
        set => SetField(ref _waitForCompletion, value);
    }

    public int CompletionTimeoutMilliseconds
    {
        get => _completionTimeoutMilliseconds;
        set => SetField(ref _completionTimeoutMilliseconds, value);
    }

    public double CompletionTolerance
    {
        get => _completionTolerance;
        set
        {
            if (SetField(ref _completionTolerance, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompletionCondition)));
            }
        }
    }

    public bool AbsolutePositionMode
    {
        get => _absolutePositionMode;
        set => SetField(ref _absolutePositionMode, value);
    }

    public string CompletionCondition => $"dmc_check_done = 1，目标误差 ≤ {CompletionTolerance:0.###}";

    public void LoadFrom(MotionMoveProfile profile)
    {
        StartVelocity = profile.StartVelocity;
        StopVelocity = profile.StopVelocity;
        AccelerationMilliseconds = profile.AccelerationSeconds * 1000;
        DecelerationMilliseconds = profile.DecelerationSeconds * 1000;
        STimeMilliseconds = profile.STimeSeconds * 1000;
        DecelerationStopMilliseconds = profile.DecelerationStopSeconds * 1000;
        WaitForCompletion = profile.WaitForCompletion;
        CompletionTimeoutMilliseconds = profile.CompletionTimeoutMilliseconds;
        CompletionTolerance = profile.CompletionTolerance;
        AbsolutePositionMode = profile.AbsolutePositionMode;
    }

    public void ApplyTo(MotionMoveProfile profile)
    {
        if (CompletionTimeoutMilliseconds is < 100 or > 600000)
        {
            throw new InvalidDataException("等待完成超时时间必须在 100 到 600000 ms 之间。");
        }


        if (!double.IsFinite(CompletionTolerance) || CompletionTolerance <= 0)
        {
            throw new InvalidDataException("到位允许误差必须是大于 0 的有限数值。");
        }

        profile.StartVelocity = StartVelocity;
        profile.StopVelocity = StopVelocity;
        profile.AccelerationSeconds = AccelerationMilliseconds / 1000;
        profile.DecelerationSeconds = DecelerationMilliseconds / 1000;
        profile.STimeSeconds = STimeMilliseconds / 1000;
        profile.DecelerationStopSeconds = DecelerationStopMilliseconds / 1000;
        profile.WaitForCompletion = WaitForCompletion;
        profile.CompletionTimeoutMilliseconds = CompletionTimeoutMilliseconds;
        profile.CompletionTolerance = CompletionTolerance;
        profile.AbsolutePositionMode = AbsolutePositionMode;
        profile.Validate();
    }

    public void ResetProfile()
    {
        StartVelocity = 0;
        StopVelocity = 0;
        AccelerationMilliseconds = 100;
        DecelerationMilliseconds = 100;
        STimeMilliseconds = 0;
        DecelerationStopMilliseconds = 100;
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
