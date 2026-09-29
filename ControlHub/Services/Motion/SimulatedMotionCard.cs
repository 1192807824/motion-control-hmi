namespace ControlHub.Services.Motion;

public sealed class SimulatedMotionCard : IMotionCard
{
    private readonly object _sync = new();
    private readonly MotionCardOptions _options;
    private readonly List<SimulatedAxis> _axes;
    private readonly double[] _analogInputs;
    private readonly double[] _analogOutputs;
    private readonly Dictionary<int, uint> _digitalOutputPorts = [];
    private readonly Dictionary<int, int[]> _linearAxesByCoordinateSystem = [];

    public SimulatedMotionCard(MotionCardOptions options)
    {
        _options = options;
        _axes = Enumerable.Range(0, options.AxisCount).Select(_ => new SimulatedAxis()).ToList();
        _analogInputs = new double[options.SimulationAnalogInputCount];
        _analogOutputs = new double[options.SimulationAnalogOutputCount];
    }

    public bool IsOpen { get; private set; }

    public int AxisCount => _axes.Count;

    public int DigitalInputCount => _options.SimulationDigitalInputCount;

    public int DigitalOutputCount => _options.SimulationDigitalOutputCount;

    public int AnalogInputCount => _analogInputs.Length;

    public int AnalogOutputCount => _analogOutputs.Length;

    public MotionCardConnectionInfo Open()
    {
        lock (_sync)
        {
            IsOpen = true;
            return new MotionCardConnectionInfo(
                _options.CardNo ?? 0,
                1,
                [new MotionCardDescriptor(_options.CardNo ?? 0, 0)],
                AxisCount,
                DigitalInputCount,
                DigitalOutputCount,
                AnalogInputCount,
                AnalogOutputCount,
                true);
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            IsOpen = false;
        }
    }

    public ushort ReadBusErrorCode()
    {
        lock (_sync)
        {
            EnsureOpen();
            return 0;
        }
    }

    public MotionAxisSnapshot ReadAxis(int hardwareAxisNo)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            axis.Update();
            return new MotionAxisSnapshot(
                hardwareAxisNo,
                axis.Position,
                axis.Position,
                axis.Target,
                axis.Speed,
                axis.IsMoving,
                axis.ServoEnabled,
                axis.Homed,
                axis.Alarm,
                axis.PositiveLimit,
                axis.NegativeLimit,
                false,
                Math.Abs(axis.Position) < 0.0001,
                axis.Alarm ? (ushort)7 : axis.ServoEnabled ? (ushort)4 : (ushort)1,
                axis.RunMode,
                axis.Alarm ? (ushort)1 : (ushort)0,
                axis.StopReason);
        }
    }

    public uint ReadDigitalInputs(int portNo)
    {
        lock (_sync)
        {
            EnsureOpen();
            return 0;
        }
    }

    public uint ReadDigitalOutputs(int portNo)
    {
        lock (_sync)
        {
            EnsureOpen();
            return _digitalOutputPorts.GetValueOrDefault(portNo);
        }
    }

    public void WriteDigitalOutput(int bitNo, bool enabled)
    {
        lock (_sync)
        {
            EnsureOpen();
            var maximumBitNo = Math.Max(DigitalOutputCount, _options.DigitalOutputStartBit + DigitalOutputCount);
            if (bitNo < 0 || bitNo >= maximumBitNo)
            {
                throw new ArgumentOutOfRangeException(nameof(bitNo));
            }

            var portNo = bitNo / 32;
            var portBitNo = bitNo % 32;
            var portState = _digitalOutputPorts.GetValueOrDefault(portNo);
            if (enabled)
            {
                portState |= 1u << portBitNo;
            }
            else
            {
                portState &= ~(1u << portBitNo);
            }

            _digitalOutputPorts[portNo] = portState;
        }
    }

    public double ReadAnalogInput(int channel)
    {
        lock (_sync)
        {
            EnsureAnalogChannel(channel, _analogInputs.Length);
            return _analogInputs[channel];
        }
    }

    public double ReadAnalogOutput(int channel)
    {
        lock (_sync)
        {
            EnsureAnalogChannel(channel, _analogOutputs.Length);
            return _analogOutputs[channel];
        }
    }

    public void WriteAnalogOutput(int channel, double value)
    {
        lock (_sync)
        {
            EnsureAnalogChannel(channel, _analogOutputs.Length);
            if (!double.IsFinite(value) || value < _options.AnalogOutputMinimum || value > _options.AnalogOutputMaximum)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _analogOutputs[channel] = value;
        }
    }

    public void ServoOn(int hardwareAxisNo, bool enabled)
    {
        lock (_sync)
        {
            var axis = GetAxis(hardwareAxisNo);
            if (!enabled && axis.IsMoving)
            {
                throw new MotionCardException(
                    $"仿真硬件轴 {hardwareAxisNo} 正在运动，必须先停止并确认后才能解除使能。");
            }

            axis.ServoEnabled = enabled;
        }
    }

    public void SetAllServos(bool enabled)
    {
        lock (_sync)
        {
            EnsureOpen();
            if (!enabled && _axes.Any(axis => axis.IsMoving))
            {
                throw new MotionCardException("仍有仿真轴在运动，必须先停止并确认后才能解除全轴使能。");
            }

            foreach (var axis in _axes)
            {
                axis.ServoEnabled = enabled;
            }
        }
    }

    public void Home(int hardwareAxisNo)
    {
        Home(hardwareAxisNo, _options.GetHomeProfile(hardwareAxisNo));
    }

    public void Home(int hardwareAxisNo, MotionHomeProfile profile)
    {
        lock (_sync)
        {
            var axis = GetReadyAxis(hardwareAxisNo);
            EnsureAxisStopped(axis, hardwareAxisNo);
            profile.Validate(requireEnabled: true);
            axis.Homed = false;
            axis.StartMove(0, Math.Max(profile.HighVelocity, profile.LowVelocity), runMode: 3, markHomedOnCompletion: true);
        }
    }

    public void Jog(int hardwareAxisNo, double velocity)
    {
        lock (_sync)
        {
            var axis = GetReadyAxis(hardwareAxisNo);
            EnsureAxisStopped(axis, hardwareAxisNo);
            if (!double.IsFinite(velocity) || velocity == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(velocity));
            }

            axis.StartContinuous(velocity);
        }
    }

    public void MoveRelative(int hardwareAxisNo, double distance, double velocity)
    {
        lock (_sync)
        {
            var axis = GetReadyAxis(hardwareAxisNo);
            EnsureAxisStopped(axis, hardwareAxisNo);
            if (!double.IsFinite(distance) || distance == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }

            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(velocity));
            }

            if (distance > 0 && axis.PositiveLimit)
            {
                throw new MotionCardException("仿真轴正限位已触发，禁止继续正向运动。");
            }

            if (distance < 0 && axis.NegativeLimit)
            {
                throw new MotionCardException("仿真轴负限位已触发，禁止继续负向运动。");
            }

            axis.StartMove(axis.Position + distance, velocity, runMode: 1, markHomedOnCompletion: false);
        }
    }

    public void MoveRelativeSynchronized(
        IReadOnlyList<int> hardwareAxisNos,
        IReadOnlyList<double> distances,
        IReadOnlyList<double> velocities)
    {
        ArgumentNullException.ThrowIfNull(hardwareAxisNos);
        ArgumentNullException.ThrowIfNull(distances);
        ArgumentNullException.ThrowIfNull(velocities);

        if (hardwareAxisNos.Count == 0 ||
            hardwareAxisNos.Count != distances.Count ||
            hardwareAxisNos.Count != velocities.Count)
        {
            throw new ArgumentException("同步相对移动的轴号、脉冲和速度数量必须一致且不能为空。");
        }

        lock (_sync)
        {
            var moves = new List<(SimulatedAxis Axis, double Distance, double Velocity)>(hardwareAxisNos.Count);
            for (var index = 0; index < hardwareAxisNos.Count; index++)
            {
                var axis = GetReadyAxis(hardwareAxisNos[index]);
                EnsureAxisStopped(axis, hardwareAxisNos[index]);
                var distance = distances[index];
                var velocity = velocities[index];
                if (!double.IsFinite(distance) || distance == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(distances));
                }

                if (!double.IsFinite(velocity) || velocity <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(velocities));
                }

                if (distance > 0 && axis.PositiveLimit)
                {
                    throw new MotionCardException("仿真轴正限位已触发，禁止继续正向运动。");
                }

                if (distance < 0 && axis.NegativeLimit)
                {
                    throw new MotionCardException("仿真轴负限位已触发，禁止继续负向运动。");
                }

                moves.Add((axis, distance, velocity));
            }

            foreach (var move in moves)
            {
                move.Axis.StartMove(
                    move.Axis.Position + move.Distance,
                    move.Velocity,
                    runMode: 1,
                    markHomedOnCompletion: false);
            }
        }
    }

    public void MoveLinearAbsolute(
        int coordinateSystemNo,
        IReadOnlyList<int> hardwareAxisNos,
        IReadOnlyList<double> targetPositions,
        IReadOnlyList<double> maximumAxisVelocities)
    {
        ArgumentNullException.ThrowIfNull(hardwareAxisNos);
        ArgumentNullException.ThrowIfNull(targetPositions);
        ArgumentNullException.ThrowIfNull(maximumAxisVelocities);

        if (coordinateSystemNo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coordinateSystemNo));
        }

        if (hardwareAxisNos.Count != 2 ||
            hardwareAxisNos.Count != targetPositions.Count ||
            hardwareAxisNos.Count != maximumAxisVelocities.Count ||
            hardwareAxisNos.Distinct().Count() != hardwareAxisNos.Count)
        {
            throw new ArgumentException("XY直线插补必须提供两根不同轴及对应的目标位置和最大速度。");
        }

        lock (_sync)
        {
            if (_linearAxesByCoordinateSystem.TryGetValue(coordinateSystemNo, out var activeAxisNos))
            {
                var coordinateMoving = activeAxisNos.Any(axisNo =>
                {
                    var activeAxis = GetAxis(axisNo);
                    activeAxis.Update();
                    return activeAxis.IsMoving;
                });
                if (coordinateMoving)
                {
                    throw new MotionCardException($"仿真插补坐标系 {coordinateSystemNo} 正在运动。");
                }
            }

            var axes = new SimulatedAxis[hardwareAxisNos.Count];
            var distances = new double[hardwareAxisNos.Count];
            for (var index = 0; index < hardwareAxisNos.Count; index++)
            {
                var target = targetPositions[index];
                var axisVelocity = maximumAxisVelocities[index];
                if (!double.IsFinite(target))
                {
                    throw new ArgumentOutOfRangeException(nameof(targetPositions));
                }

                if (!double.IsFinite(axisVelocity) || axisVelocity <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(maximumAxisVelocities));
                }

                var axis = GetReadyAxis(hardwareAxisNos[index]);
                EnsureAxisStopped(axis, hardwareAxisNos[index]);
                var distance = target - axis.Position;
                if (distance > 0 && axis.PositiveLimit)
                {
                    throw new MotionCardException("仿真轴正限位已触发，禁止继续正向运动。");
                }

                if (distance < 0 && axis.NegativeLimit)
                {
                    throw new MotionCardException("仿真轴负限位已触发，禁止继续负向运动。");
                }

                axes[index] = axis;
                distances[index] = distance;
            }

            var pathLength = Math.Sqrt(distances.Sum(distance => distance * distance));
            if (!double.IsFinite(pathLength) || pathLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetPositions), "插补路径长度必须大于0。");
            }

            var maximumVectorVelocity = Enumerable.Range(0, distances.Length)
                .Where(index => Math.Abs(distances[index]) > 0)
                .Min(index => maximumAxisVelocities[index] /
                              (Math.Abs(distances[index]) / pathLength));
            var durationSeconds = Math.Max(pathLength / maximumVectorVelocity, 0.05);
            for (var index = 0; index < axes.Length; index++)
            {
                axes[index].StartMoveWithDuration(
                    targetPositions[index],
                    durationSeconds,
                    runMode: 4);
            }

            _linearAxesByCoordinateSystem[coordinateSystemNo] = hardwareAxisNos.ToArray();
        }
    }

    public void StopLinearInterpolation(int coordinateSystemNo, bool emergency = false)
    {
        if (coordinateSystemNo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coordinateSystemNo));
        }

        lock (_sync)
        {
            EnsureOpen();
            if (!_linearAxesByCoordinateSystem.Remove(coordinateSystemNo, out var axisNos))
            {
                return;
            }

            foreach (var axisNo in axisNos)
            {
                GetAxis(axisNo).Stop(emergency ? 1 : 0);
            }
        }
    }

    public void MoveAbsolute(int hardwareAxisNo, double position, double velocity)
    {
        lock (_sync)
        {
            var axis = GetReadyAxis(hardwareAxisNo);
            EnsureAxisStopped(axis, hardwareAxisNo);
            if (!double.IsFinite(position))
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(velocity));
            }

            var direction = Math.Sign(position - axis.Position);
            if (direction > 0 && axis.PositiveLimit)
            {
                throw new MotionCardException("仿真轴正限位已触发，禁止继续正向运动。");
            }

            if (direction < 0 && axis.NegativeLimit)
            {
                throw new MotionCardException("仿真轴负限位已触发，禁止继续负向运动。");
            }

            axis.StartMove(position, velocity, runMode: 1, markHomedOnCompletion: false);
        }
    }

    public void Stop(int hardwareAxisNo, bool emergency = false)
    {
        lock (_sync)
        {
            GetAxis(hardwareAxisNo).Stop(emergency ? 1 : 0);
        }
    }

    public void EmergencyStop()
    {
        lock (_sync)
        {
            EnsureOpen();
            foreach (var axis in _axes)
            {
                axis.Stop(1);
            }
        }
    }

    public void ClearAlarms(IEnumerable<int> hardwareAxisNumbers)
    {
        lock (_sync)
        {
            EnsureOpen();
            foreach (var hardwareAxisNo in hardwareAxisNumbers.Distinct())
            {
                var axis = GetAxis(hardwareAxisNo);
                axis.Alarm = false;
                axis.StopReason = 0;
            }
        }
    }

    public void Dispose()
    {
        Close();
    }

    private SimulatedAxis GetReadyAxis(int hardwareAxisNo)
    {
        var axis = GetAxis(hardwareAxisNo);
        if (!axis.ServoEnabled)
        {
            throw new MotionCardException($"仿真硬件轴 {hardwareAxisNo} 尚未使能。");
        }

        if (axis.Alarm)
        {
            throw new MotionCardException($"仿真硬件轴 {hardwareAxisNo} 存在报警。");
        }

        return axis;
    }

    private SimulatedAxis GetAxis(int hardwareAxisNo)
    {
        EnsureOpen();
        if (hardwareAxisNo < 0 || hardwareAxisNo >= AxisCount)
        {
            throw new ArgumentOutOfRangeException(nameof(hardwareAxisNo));
        }

        return _axes[hardwareAxisNo];
    }

    private static void EnsureAxisStopped(SimulatedAxis axis, int hardwareAxisNo)
    {
        axis.Update();
        if (axis.IsMoving)
        {
            throw new MotionCardException($"仿真硬件轴 {hardwareAxisNo} 正在运动，拒绝重复下发运动命令。");
        }
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("仿真运动控制卡尚未打开。");
        }
    }

    private void EnsureAnalogChannel(int channel, int count)
    {
        EnsureOpen();
        if (channel < 0 || channel >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }

    private sealed class SimulatedAxis
    {
        private DateTime _lastUpdate = DateTime.UtcNow;
        private DateTime? _moveStarted;
        private DateTime? _moveEnds;
        private double _moveStartPosition;
        private bool _continuous;
        private bool _markHomedOnCompletion;

        public double Position { get; private set; }

        public double Target { get; private set; }

        public double Speed { get; private set; }

        public bool IsMoving { get; private set; }

        public bool ServoEnabled { get; set; }

        public bool Homed { get; set; }

        public bool Alarm { get; set; }

        public bool PositiveLimit { get; set; }

        public bool NegativeLimit { get; set; }

        public ushort RunMode { get; private set; }

        public int StopReason { get; set; }

        public void StartMove(double target, double velocity, ushort runMode, bool markHomedOnCompletion)
        {
            Update();
            _continuous = false;
            _moveStartPosition = Position;
            Target = target;
            var durationSeconds = Math.Max(Math.Abs(Target - Position) / Math.Abs(velocity), 0.05);
            _moveStarted = DateTime.UtcNow;
            _moveEnds = _moveStarted.Value.AddSeconds(durationSeconds);
            _markHomedOnCompletion = markHomedOnCompletion;
            Speed = Math.Sign(Target - Position) * Math.Abs(velocity);
            IsMoving = Math.Abs(Target - Position) > 0.000001;
            RunMode = IsMoving ? runMode : (ushort)0;
            StopReason = 0;
            if (!IsMoving && markHomedOnCompletion)
            {
                Homed = true;
            }
        }

        public void StartMoveWithDuration(double target, double durationSeconds, ushort runMode)
        {
            Update();
            _continuous = false;
            _moveStartPosition = Position;
            Target = target;
            _moveStarted = DateTime.UtcNow;
            _moveEnds = _moveStarted.Value.AddSeconds(Math.Max(durationSeconds, 0.05));
            _markHomedOnCompletion = false;
            Speed = Math.Sign(Target - Position) *
                    Math.Abs(Target - Position) / Math.Max(durationSeconds, 0.05);
            IsMoving = Math.Abs(Target - Position) > 0.000001;
            RunMode = IsMoving ? runMode : (ushort)0;
            StopReason = 0;
        }

        public void StartContinuous(double velocity)
        {
            Update();
            _continuous = true;
            _moveStarted = null;
            _moveEnds = null;
            _lastUpdate = DateTime.UtcNow;
            Speed = velocity;
            Target = Position;
            IsMoving = true;
            RunMode = 2;
            StopReason = 0;
        }

        public void Stop(int reason)
        {
            Update();
            Target = Position;
            Speed = 0;
            IsMoving = false;
            RunMode = 0;
            StopReason = reason;
            _continuous = false;
            _moveStarted = null;
            _moveEnds = null;
            _markHomedOnCompletion = false;
        }

        public void Update()
        {
            var now = DateTime.UtcNow;
            if (!IsMoving)
            {
                _lastUpdate = now;
                return;
            }

            if (_continuous)
            {
                Position += Speed * (now - _lastUpdate).TotalSeconds;
                Target = Position;
                _lastUpdate = now;
                return;
            }

            if (_moveStarted is null || _moveEnds is null || now >= _moveEnds)
            {
                Position = Target;
                Speed = 0;
                IsMoving = false;
                RunMode = 0;
                if (_markHomedOnCompletion)
                {
                    Homed = true;
                }

                _markHomedOnCompletion = false;
                _lastUpdate = now;
                return;
            }

            var total = (_moveEnds.Value - _moveStarted.Value).TotalSeconds;
            var elapsed = (now - _moveStarted.Value).TotalSeconds;
            Position = _moveStartPosition + (Target - _moveStartPosition) * Math.Clamp(elapsed / total, 0, 1);
            _lastUpdate = now;
        }
    }
}
