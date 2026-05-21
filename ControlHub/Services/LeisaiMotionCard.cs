using System.Runtime.InteropServices;

namespace ControlHub.Services;

public sealed class LeisaiMotionCard : IMotionCard
{
    private readonly bool _simulationMode;

    public LeisaiMotionCard(bool simulationMode = true)
    {
        _simulationMode = simulationMode;
    }

    public bool Open()
    {
        return _simulationMode || LTDMC.dmc_board_init() > 0;
    }

    public void Close()
    {
        if (!_simulationMode)
        {
            LTDMC.dmc_board_close();
        }
    }

    public void ServoOn(int axisNo, bool enabled)
    {
        if (!_simulationMode)
        {
            LTDMC.dmc_write_sevon_pin(0, (ushort)axisNo, enabled ? (ushort)0 : (ushort)1);
        }
    }

    public void Home(int axisNo)
    {
        if (!_simulationMode)
        {
            LTDMC.dmc_home_move(0, (ushort)axisNo);
        }
    }

    public void Jog(int axisNo, double velocity)
    {
        if (_simulationMode)
        {
            return;
        }

        LTDMC.dmc_set_profile_unit(0, (ushort)axisNo, 0, Math.Abs(velocity), 0.1, 0.1, 0);
        LTDMC.dmc_vmove(0, (ushort)axisNo, velocity >= 0 ? (ushort)1 : (ushort)0);
    }

    public void Stop(int axisNo)
    {
        if (!_simulationMode)
        {
            LTDMC.dmc_stop(0, (ushort)axisNo, 0);
        }
    }

    public void EmergencyStop()
    {
        if (!_simulationMode)
        {
            LTDMC.dmc_emg_stop(0);
        }
    }

    private static class LTDMC
    {
        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_board_init();

        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_board_close();

        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_write_sevon_pin(ushort cardNo, ushort axis, ushort onOff);

        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_set_profile_unit(ushort cardNo, ushort axis, double minVel, double maxVel, double tAcc, double tDec, double stopVel);

        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_vmove(ushort cardNo, ushort axis, ushort dir);

        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_stop(ushort cardNo, ushort axis, ushort stopMode);

        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_home_move(ushort cardNo, ushort axis);

        [DllImport("LTDMC.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_emg_stop(ushort cardNo);
    }
}
