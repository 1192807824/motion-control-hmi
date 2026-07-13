using System.Diagnostics;
using System.IO;

namespace ControlHub.Services.Motion;

public sealed class LeisaiMotionCard : IMotionCard
{
    private const ushort EtherCatPort = 2;
    private const ushort RingRedundancyDisconnectedWarning = 0x0228;
    private const ushort AllEtherCatAxesSentinel = 255;
    private const ushort EnabledStateMachine = 4;

    private readonly object _sync = new();
    private readonly MotionCardOptions _options;
    private ushort _cardNo;
    private int _detectedCardCount;
    private MotionCardDescriptor[] _detectedCards = [];

    public LeisaiMotionCard(MotionCardOptions options)
    {
        _options = options;
    }

    public bool IsOpen { get; private set; }

    public int AxisCount { get; private set; }

    public int DigitalInputCount { get; private set; }

    public int DigitalOutputCount { get; private set; }

    public int AnalogInputCount { get; private set; }

    public int AnalogOutputCount { get; private set; }

    public MotionCardConnectionInfo Open()
    {
        lock (_sync)
        {
            if (IsOpen)
            {
                return ConnectionInfo();
            }

            try
            {
                var cardCount = LeisaiNative.dmc_board_init();
                if (cardCount == 0)
                {
                    throw new MotionCardException("未检测到雷赛运动控制卡，或控制卡初始化异常。", "dmc_board_init");
                }

                _detectedCardCount = Math.Abs(cardCount);
                if (cardCount < 0)
                {
                    var duplicateCardNo = Math.Abs(cardCount) - 1;
                    throw new MotionCardException(
                        $"检测到重复的硬件卡号 {duplicateCardNo}，请检查板卡拨码开关。",
                        "dmc_board_init",
                        cardCount);
                }

                var listedCardCount = (ushort)0;
                var cardTypes = new uint[8];
                var cardIds = new ushort[8];
                EnsureSuccess(
                    LeisaiNative.dmc_get_CardInfList(ref listedCardCount, cardTypes, cardIds),
                    "dmc_get_CardInfList");

                if (listedCardCount == 0)
                {
                    throw new MotionCardException("控制卡初始化成功，但没有返回可用的硬件卡号。", "dmc_get_CardInfList");
                }

                _detectedCards = Enumerable.Range(0, listedCardCount)
                    .Select(index => new MotionCardDescriptor(cardIds[index], cardTypes[index]))
                    .ToArray();

                _cardNo = _options.CardNo ?? cardIds[0];
                if (!cardIds.Take(listedCardCount).Contains(_cardNo))
                {
                    throw new MotionCardException(
                        $"配置的卡号 {_cardNo} 不在已检测卡号列表 [{string.Join(", ", cardIds.Take(listedCardCount))}] 中。",
                        "dmc_get_CardInfList");
                }

                uint totalAxes = 0;
                EnsureSuccess(LeisaiNative.nmc_get_total_axes(_cardNo, ref totalAxes), "nmc_get_total_axes");
                AxisCount = checked((int)totalAxes);
                if (AxisCount <= 0)
                {
                    throw new MotionCardException($"卡号 {_cardNo} 未返回有效 EtherCAT 轴数。", "nmc_get_total_axes");
                }

                ushort digitalInputs = 0;
                ushort digitalOutputs = 0;
                // dmc_get_total_ionum only reports the controller's local I/O (8/8 on
                // DMC-E3064S). The motion card UI and the 32-bit port APIs expose the
                // complete EtherCAT I/O image, whose size is reported by nmc_*.
                EnsureSuccess(
                    LeisaiNative.nmc_get_total_ionum(_cardNo, ref digitalInputs, ref digitalOutputs),
                    "nmc_get_total_ionum");
                DigitalInputCount = digitalInputs;
                DigitalOutputCount = digitalOutputs;

                ushort analogInputs = 0;
                ushort analogOutputs = 0;
                EnsureSuccess(
                    LeisaiNative.dmc_get_total_adcnum(_cardNo, ref analogInputs, ref analogOutputs),
                    "dmc_get_total_adcnum");
                AnalogInputCount = analogInputs;
                AnalogOutputCount = analogOutputs;

                IsOpen = true;
                return ConnectionInfo();
            }
            catch (DllNotFoundException exception)
            {
                CloseAfterFailedOpen();
                throw new MotionCardException(
                    "找不到 LTDMC.dll。请安装与程序位数匹配的雷赛运行库，并将 LTDMC.dll 放到程序目录或系统 PATH 中。",
                    "加载 LTDMC.dll",
                    innerException: exception);
            }
            catch (BadImageFormatException exception)
            {
                CloseAfterFailedOpen();
                throw new MotionCardException(
                    "LTDMC.dll 与当前程序位数不匹配。请统一使用 x64 或 x86 版本的程序、驱动和 DLL。",
                    "加载 LTDMC.dll",
                    innerException: exception);
            }
            catch (EntryPointNotFoundException exception)
            {
                CloseAfterFailedOpen();
                throw new MotionCardException(
                    "LTDMC.dll 版本不包含所需 EtherCAT 接口，请安装与本项目手册/控制卡匹配的新版运行库。",
                    "加载 LTDMC.dll",
                    innerException: exception);
            }
            catch
            {
                CloseAfterFailedOpen();
                throw;
            }
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (!IsOpen)
            {
                return;
            }

            var result = LeisaiNative.dmc_board_close();
            IsOpen = false;
            AxisCount = 0;
            DigitalInputCount = 0;
            DigitalOutputCount = 0;
            AnalogInputCount = 0;
            AnalogOutputCount = 0;
            _detectedCardCount = 0;
            _detectedCards = [];
            EnsureSuccess(result, "dmc_board_close");
        }
    }

    public ushort ReadBusErrorCode()
    {
        lock (_sync)
        {
            EnsureOpen();
            ushort errorCode = 0;
            EnsureSuccess(LeisaiNative.nmc_get_errcode(_cardNo, EtherCatPort, ref errorCode), "nmc_get_errcode");
            return errorCode;
        }
    }

    public MotionAxisSnapshot ReadAxis(int hardwareAxisNo)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            ushort stateMachine = 0;
            ushort axisError = 0;
            ushort homeResult = 0;
            ushort runMode = 0;
            double commandPosition = 0;
            double feedbackPosition = 0;
            double targetPosition = 0;
            double speed = 0;
            uint ioState = 0;
            var stopReason = 0;

            EnsureSuccess(LeisaiNative.nmc_get_axis_state_machine(_cardNo, axis, ref stateMachine), "nmc_get_axis_state_machine");
            EnsureSuccess(LeisaiNative.nmc_get_axis_errcode(_cardNo, axis, ref axisError), "nmc_get_axis_errcode");
            EnsureSuccess(LeisaiNative.dmc_get_position_unit(_cardNo, axis, ref commandPosition), "dmc_get_position_unit");
            EnsureSuccess(LeisaiNative.dmc_get_encoder_unit(_cardNo, axis, ref feedbackPosition), "dmc_get_encoder_unit");
            EnsureSuccess(LeisaiNative.dmc_read_current_speed_unit(_cardNo, axis, ref speed), "dmc_read_current_speed_unit");
            EnsureSuccess(LeisaiNative.dmc_get_axis_run_mode(_cardNo, axis, ref runMode), "dmc_get_axis_run_mode");
            EnsureSuccess(LeisaiNative.dmc_get_home_result(_cardNo, axis, ref homeResult), "dmc_get_home_result");
            EnsureSuccess(LeisaiNative.dmc_axis_io_status_ex(_cardNo, axis, ref ioState), "dmc_axis_io_status_ex");
            EnsureSuccess(LeisaiNative.dmc_get_stop_reason(_cardNo, axis, ref stopReason), "dmc_get_stop_reason");

            var doneState = LeisaiNative.dmc_check_done(_cardNo, axis);
            if (doneState is not (0 or 1))
            {
                throw NativeFailure("dmc_check_done", doneState);
            }

            targetPosition = commandPosition;
            if (runMode == 1 && doneState == 0)
            {
                EnsureSuccess(
                    LeisaiNative.dmc_get_target_position_unit(_cardNo, axis, ref targetPosition),
                    "dmc_get_target_position_unit");
            }

            var hardwareAlarm = IsBitSet(ioState, 0);
            var positiveLimit = IsBitSet(ioState, 1) || IsBitSet(ioState, 6);
            var negativeLimit = IsBitSet(ioState, 2) || IsBitSet(ioState, 7);
            var emergencyInput = IsBitSet(ioState, 3);
            var stateMachineAlarm = stateMachine is 6 or 7;

            return new MotionAxisSnapshot(
                hardwareAxisNo,
                commandPosition,
                feedbackPosition,
                targetPosition,
                speed,
                doneState == 0,
                stateMachine == EnabledStateMachine,
                homeResult == 1,
                hardwareAlarm || stateMachineAlarm || axisError != 0,
                positiveLimit,
                negativeLimit,
                emergencyInput,
                IsBitSet(ioState, 4),
                stateMachine,
                runMode,
                axisError,
                stopReason);
        }
    }

    public uint ReadDigitalInputs(int portNo)
    {
        lock (_sync)
        {
            EnsureOpen();
            if (portNo is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(portNo));
            }

            uint state = 0;
            EnsureSuccess(LeisaiNative.dmc_read_inport_ex(_cardNo, (ushort)portNo, ref state), "dmc_read_inport_ex");
            return state;
        }
    }

    public uint ReadDigitalOutputs(int portNo)
    {
        lock (_sync)
        {
            EnsureOpen();
            if (portNo is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(portNo));
            }

            uint state = 0;
            EnsureSuccess(LeisaiNative.dmc_read_outport_ex(_cardNo, (ushort)portNo, ref state), "dmc_read_outport_ex");
            return state;
        }
    }

    public void WriteDigitalOutput(int bitNo, bool enabled)
    {
        lock (_sync)
        {
            EnsureOpen();
            if (bitNo < 0 || bitNo >= DigitalOutputCount)
            {
                throw new ArgumentOutOfRangeException(nameof(bitNo), $"数字输出点必须在 0 到 {DigitalOutputCount - 1} 之间。");
            }

            EnsureSuccess(
                LeisaiNative.dmc_write_outbit(_cardNo, (ushort)bitNo, enabled ? (ushort)1 : (ushort)0),
                "dmc_write_outbit");
        }
    }

    public double ReadAnalogInput(int channel)
    {
        lock (_sync)
        {
            ValidateAnalogChannel(channel, AnalogInputCount, nameof(channel));
            double value = 0;
            EnsureSuccess(LeisaiNative.dmc_get_ad_input(_cardNo, (ushort)channel, ref value), "dmc_get_ad_input");
            return value;
        }
    }

    public double ReadAnalogOutput(int channel)
    {
        lock (_sync)
        {
            ValidateAnalogChannel(channel, AnalogOutputCount, nameof(channel));
            double value = 0;
            EnsureSuccess(LeisaiNative.dmc_get_da_output(_cardNo, (ushort)channel, ref value), "dmc_get_da_output");
            return value;
        }
    }

    public void WriteAnalogOutput(int channel, double value)
    {
        lock (_sync)
        {
            ValidateAnalogChannel(channel, AnalogOutputCount, nameof(channel));
            if (!double.IsFinite(value) || value < _options.AnalogOutputMinimum || value > _options.AnalogOutputMaximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"模拟输出必须在 {_options.AnalogOutputMinimum:0.###} 到 {_options.AnalogOutputMaximum:0.###} 之间。");
            }

            EnsureSuccess(LeisaiNative.dmc_set_da_output(_cardNo, (ushort)channel, value), "dmc_set_da_output");
        }
    }

    public void ServoOn(int hardwareAxisNo, bool enabled)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            EnsureBusReady();

            var doneState = LeisaiNative.dmc_check_done(_cardNo, axis);
            if (doneState is not (0 or 1))
            {
                throw NativeFailure("dmc_check_done", doneState);
            }

            if (!enabled && doneState == 0)
            {
                throw new MotionCardException(
                    $"硬件轴 {hardwareAxisNo} 正在运动，必须先停止并确认 dmc_check_done = 1 后才能解除使能。",
                    "dmc_check_done");
            }

            SetServoCommand(axis, enabled);
            WaitForServoState([axis], enabled);
        }
    }

    public void SetAllServos(bool enabled)
    {
        lock (_sync)
        {
            EnsureOpen();
            EnsureBusReady();

            // Axis 255 is the SDK's all-axis sentinel, not an addressable hardware axis.
            // Refuse an impossible count before converting indexes to ushort so that a
            // per-axis safety operation can never accidentally become a card-wide command.
            if (AxisCount > AllEtherCatAxesSentinel)
            {
                throw new MotionCardException(
                    $"控制卡返回 {AxisCount} 个硬件轴，超过逐轴接口可安全寻址的 0..{AllEtherCatAxesSentinel - 1} 范围。",
                    "nmc_get_total_axes");
            }

            var configuredAxes = Enumerable.Range(0, Math.Min(AxisCount, _options.AxisCount))
                .Select(value => checked((ushort)value))
                .ToArray();
            if (enabled)
            {
                var newlyEnabledAxes = new List<ushort>(configuredAxes.Length);
                try
                {
                    foreach (var axis in configuredAxes)
                    {
                        ushort stateMachine = 0;
                        EnsureSuccess(
                            LeisaiNative.nmc_get_axis_state_machine(_cardNo, axis, ref stateMachine),
                            "nmc_get_axis_state_machine");
                        if (stateMachine == EnabledStateMachine)
                        {
                            continue;
                        }

                        SetServoCommand(axis, true);
                        newlyEnabledAxes.Add(axis);
                    }

                    WaitForServoState(configuredAxes, true);
                }
                catch (Exception enableFailure)
                {
                    var rollbackFailures = new List<Exception>();
                    for (var index = newlyEnabledAxes.Count - 1; index >= 0; index--)
                    {
                        var axis = newlyEnabledAxes[index];
                        try
                        {
                            EnsureAxisStopped(axis);
                            SetServoCommand(axis, false);
                            WaitForServoState([axis], false);
                        }
                        catch (Exception rollbackFailure)
                        {
                            rollbackFailures.Add(
                                new MotionCardException(
                                    $"硬件轴 {axis} 在全轴使能失败后的回滚解除使能中失败。",
                                    "全轴使能回滚",
                                    innerException: rollbackFailure));
                        }
                    }

                    if (rollbackFailures.Count > 0)
                    {
                        throw new AggregateException(
                            "全轴使能失败，且一个或多个本次已下发使能命令的轴回滚失败。",
                            [enableFailure, .. rollbackFailures]);
                    }

                    throw;
                }
            }
            else
            {
                var hardwareAxes = Enumerable.Range(0, AxisCount)
                    .Select(value => checked((ushort)value))
                    .ToArray();

                foreach (var axis in hardwareAxes)
                {
                    EnsureAxisStopped(axis);
                }

                foreach (var axis in hardwareAxes)
                {
                    SetServoCommand(axis, false);
                }

                WaitForServoState(hardwareAxes, false);
            }
        }
    }

    public void Home(int hardwareAxisNo)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            EnsureAxisReadyForDirection(axis, direction: 0);
            EnsureAxisStopped(axis);
            var profile = _options.GetHomeProfile(hardwareAxisNo);
            profile.Validate(requireEnabled: true);
            EnsureSuccess(LeisaiNative.dmc_clear_stop_reason(_cardNo, axis), "dmc_clear_stop_reason");
            EnsureSuccess(
                LeisaiNative.nmc_set_home_profile(
                    _cardNo,
                    axis,
                    profile.Mode,
                    profile.LowVelocity,
                    profile.HighVelocity,
                    profile.AccelerationSeconds,
                    profile.DecelerationSeconds,
                    profile.OffsetPosition),
                "nmc_set_home_profile");
            EnsureSuccess(LeisaiNative.dmc_home_move(_cardNo, axis), "dmc_home_move");
        }
    }

    public void Jog(int hardwareAxisNo, double velocity)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            ValidateVelocity(velocity, allowSigned: true);
            EnsureAxisReadyForDirection(axis, Math.Sign(velocity));
            EnsureAxisStopped(axis);
            ConfigureMove(axis, Math.Abs(velocity));
            EnsureSuccess(LeisaiNative.dmc_clear_stop_reason(_cardNo, axis), "dmc_clear_stop_reason");
            EnsureSuccess(LeisaiNative.dmc_vmove(_cardNo, axis, velocity >= 0 ? (ushort)1 : (ushort)0), "dmc_vmove");
        }
    }

    public void MoveRelative(int hardwareAxisNo, double distance, double velocity)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            if (!double.IsFinite(distance) || distance == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(distance), "相对位移必须是非零有限数值。");
            }

            ValidateVelocity(velocity, allowSigned: false);
            EnsureAxisReadyForDirection(axis, Math.Sign(distance));
            EnsureAxisStopped(axis);
            ConfigureMove(axis, velocity);
            EnsureSuccess(LeisaiNative.dmc_clear_stop_reason(_cardNo, axis), "dmc_clear_stop_reason");
            EnsureSuccess(LeisaiNative.dmc_pmove_unit(_cardNo, axis, distance, 0), "dmc_pmove_unit");
        }
    }

    public void MoveAbsolute(int hardwareAxisNo, double position, double velocity)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            if (!double.IsFinite(position))
            {
                throw new ArgumentOutOfRangeException(nameof(position), "绝对目标位置必须是有限数值。");
            }

            ValidateVelocity(velocity, allowSigned: false);
            double currentPosition = 0;
            EnsureSuccess(LeisaiNative.dmc_get_position_unit(_cardNo, axis, ref currentPosition), "dmc_get_position_unit");
            EnsureAxisReadyForDirection(axis, Math.Sign(position - currentPosition));
            EnsureAxisStopped(axis);
            ConfigureMove(axis, velocity);
            EnsureSuccess(LeisaiNative.dmc_clear_stop_reason(_cardNo, axis), "dmc_clear_stop_reason");
            EnsureSuccess(LeisaiNative.dmc_pmove_unit(_cardNo, axis, position, 1), "dmc_pmove_unit");
        }
    }

    public void Stop(int hardwareAxisNo, bool emergency = false)
    {
        lock (_sync)
        {
            StopCore(GetAxis(hardwareAxisNo), emergency);
        }
    }

    public void EmergencyStop()
    {
        lock (_sync)
        {
            EnsureOpen();
            EnsureSuccess(LeisaiNative.dmc_emg_stop(_cardNo), "dmc_emg_stop");
        }
    }

    public void ClearAlarms(IEnumerable<int> hardwareAxisNumbers)
    {
        lock (_sync)
        {
            EnsureOpen();
            EnsureSuccess(LeisaiNative.nmc_clear_errcode(_cardNo, EtherCatPort), "nmc_clear_errcode");
            foreach (var hardwareAxisNo in hardwareAxisNumbers.Distinct())
            {
                var axis = GetAxis(hardwareAxisNo);
                EnsureSuccess(LeisaiNative.nmc_clear_axis_errcode(_cardNo, axis), "nmc_clear_axis_errcode");
                EnsureSuccess(LeisaiNative.dmc_clear_stop_reason(_cardNo, axis), "dmc_clear_stop_reason");
            }
        }
    }

    public void Dispose()
    {
        Close();
    }

    private MotionCardConnectionInfo ConnectionInfo()
    {
        return new MotionCardConnectionInfo(
            _cardNo,
            _detectedCardCount,
            _detectedCards,
            AxisCount,
            DigitalInputCount,
            DigitalOutputCount,
            AnalogInputCount,
            AnalogOutputCount,
            false);
    }

    private void ConfigureMove(ushort axis, double velocity)
    {
        var profile = _options.GetMoveProfile(axis);
        if (profile.StartVelocity > velocity)
        {
            throw new InvalidDataException(
                $"硬件轴 {axis} 的启动速度 {profile.StartVelocity:0.###} 不能大于运行速度 {velocity:0.###}。");
        }

        if (profile.StopVelocity > velocity)
        {
            throw new InvalidDataException(
                $"硬件轴 {axis} 的停止速度 {profile.StopVelocity:0.###} 不能大于运行速度 {velocity:0.###}。");
        }

        EnsureSuccess(
            LeisaiNative.dmc_set_profile_unit(
                _cardNo,
                axis,
                profile.StartVelocity,
                velocity,
                profile.AccelerationSeconds,
                profile.DecelerationSeconds,
                profile.StopVelocity),
            "dmc_set_profile_unit");
        EnsureSuccess(LeisaiNative.dmc_set_s_profile(_cardNo, axis, 0, profile.STimeSeconds), "dmc_set_s_profile");
        EnsureSuccess(
            LeisaiNative.dmc_set_dec_stop_time(_cardNo, axis, profile.DecelerationStopSeconds),
            "dmc_set_dec_stop_time");
    }

    private void StopCore(ushort axis, bool emergency)
    {
        EnsureOpen();
        EnsureSuccess(LeisaiNative.dmc_stop(_cardNo, axis, emergency ? (ushort)1 : (ushort)0), "dmc_stop");
    }

    private void EnsureAxisReadyForDirection(ushort axis, int direction)
    {
        EnsureAxisReady(axis);
        uint ioState = 0;
        EnsureSuccess(LeisaiNative.dmc_axis_io_status_ex(_cardNo, axis, ref ioState), "dmc_axis_io_status_ex");
        if (direction > 0 && (IsBitSet(ioState, 1) || IsBitSet(ioState, 6)))
        {
            throw new MotionCardException($"硬件轴 {axis} 的正限位已触发，禁止继续正向运动。", "运动方向安全检查");
        }

        if (direction < 0 && (IsBitSet(ioState, 2) || IsBitSet(ioState, 7)))
        {
            throw new MotionCardException($"硬件轴 {axis} 的负限位已触发，禁止继续负向运动。", "运动方向安全检查");
        }

        if (IsBitSet(ioState, 0) || IsBitSet(ioState, 3))
        {
            throw new MotionCardException($"硬件轴 {axis} 存在伺服报警或急停输入，禁止运动。", "运动安全检查");
        }
    }

    private void EnsureAxisReady(ushort axis)
    {
        EnsureBusReady();
        ushort stateMachine = 0;
        EnsureSuccess(LeisaiNative.nmc_get_axis_state_machine(_cardNo, axis, ref stateMachine), "nmc_get_axis_state_machine");
        if (stateMachine != EnabledStateMachine)
        {
            throw new MotionCardException(
                $"硬件轴 {axis} 尚未进入操作使能状态（当前状态机 {stateMachine}，要求 4）。",
                "轴状态检查");
        }

        ushort axisError = 0;
        EnsureSuccess(LeisaiNative.nmc_get_axis_errcode(_cardNo, axis, ref axisError), "nmc_get_axis_errcode");
        if (axisError != 0)
        {
            throw new MotionCardException(
                $"硬件轴 {axis} 存在轴错误 0x{axisError:X4}，禁止运动。",
                "轴错误检查");
        }
    }

    private void EnsureAxisStopped(ushort axis)
    {
        var doneState = LeisaiNative.dmc_check_done(_cardNo, axis);
        if (doneState is not (0 or 1))
        {
            throw NativeFailure("dmc_check_done", doneState);
        }

        if (doneState == 0)
        {
            throw new MotionCardException(
                $"硬件轴 {axis} 正在运动，拒绝重复下发运动命令。",
                "轴忙检查");
        }
    }

    private void EnsureBusReady()
    {
        var busErrorCode = ReadBusErrorCode();
        if (busErrorCode != 0 && busErrorCode != RingRedundancyDisconnectedWarning)
        {
            throw new MotionCardException(
                $"EtherCAT 总线异常 0x{busErrorCode:X4}，命令未下发。请检查 ENI 配置、从站连接与总线状态。",
                "nmc_get_errcode",
                busErrorCode: busErrorCode);
        }
    }

    private void SetServoCommand(ushort axis, bool enabled)
    {
        EnsureSuccess(
            enabled
                ? LeisaiNative.nmc_set_axis_enable(_cardNo, axis)
                : LeisaiNative.nmc_set_axis_disable(_cardNo, axis),
            enabled ? "nmc_set_axis_enable" : "nmc_set_axis_disable");
    }

    private void WaitForServoState(IEnumerable<ushort> axes, bool enabled)
    {
        var axisList = axes.ToArray();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < _options.ServoEnableTimeoutMilliseconds)
        {
            var pending = new List<ushort>();
            foreach (var axis in axisList)
            {
                ushort stateMachine = 0;
                EnsureSuccess(LeisaiNative.nmc_get_axis_state_machine(_cardNo, axis, ref stateMachine), "nmc_get_axis_state_machine");
                var reached = enabled ? stateMachine == EnabledStateMachine : stateMachine != EnabledStateMachine;
                if (!reached)
                {
                    pending.Add(axis);
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            if (enabled)
            {
                foreach (var axis in pending)
                {
                    SetServoCommand(axis, true);
                }
            }

            Thread.Sleep(20);
        }

        throw new MotionCardException(
            $"轴使能状态在 {_options.ServoEnableTimeoutMilliseconds} ms 内未切换完成。",
            enabled ? "nmc_set_axis_enable" : "nmc_set_axis_disable");
    }

    private ushort GetAxis(int hardwareAxisNo)
    {
        EnsureOpen();
        if (hardwareAxisNo < 0 || hardwareAxisNo >= AxisCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hardwareAxisNo),
                hardwareAxisNo,
                $"硬件轴号必须在 0 到 {AxisCount - 1} 之间。");
        }

        return checked((ushort)hardwareAxisNo);
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("运动控制卡尚未打开。");
        }
    }

    private void CloseAfterFailedOpen()
    {
        if (_detectedCardCount > 0)
        {
            try
            {
                LeisaiNative.dmc_board_close();
            }
            catch
            {
                // Preserve the original initialization error.
            }
        }

        IsOpen = false;
        AxisCount = 0;
        DigitalInputCount = 0;
        DigitalOutputCount = 0;
        AnalogInputCount = 0;
        AnalogOutputCount = 0;
        _detectedCardCount = 0;
        _detectedCards = [];
    }

    private static bool IsBitSet(uint value, int bit)
    {
        return (value & (1u << bit)) != 0;
    }

    private static void ValidateVelocity(double velocity, bool allowSigned)
    {
        var magnitude = Math.Abs(velocity);
        if (!double.IsFinite(velocity) || magnitude <= 0 || (!allowSigned && velocity < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "速度必须是大于 0 的有限数值。");
        }
    }

    private void ValidateAnalogChannel(int channel, int channelCount, string parameterName)
    {
        EnsureOpen();
        if (channel < 0 || channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                channel,
                channelCount == 0 ? "当前控制卡未检测到该类模拟量通道。" : $"通道必须在 0 到 {channelCount - 1} 之间。");
        }
    }

    private static void EnsureSuccess(short result, string operation)
    {
        if (result != 0)
        {
            throw NativeFailure(operation, result);
        }
    }

    private static MotionCardException NativeFailure(string operation, int errorCode)
    {
        return new MotionCardException(
            $"雷赛函数 {operation} 调用失败，返回码 {errorCode} (0x{unchecked((ushort)errorCode):X4})。",
            operation,
            errorCode);
    }
}
