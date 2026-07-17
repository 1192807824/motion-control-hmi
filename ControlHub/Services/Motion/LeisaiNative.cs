using System.Runtime.InteropServices;

namespace ControlHub.Services.Motion;

internal static class LeisaiNative
{
    private const string DllName = "LTDMC.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_board_init();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_board_close();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_CardInfList(
        ref ushort cardCount,
        [Out] uint[] cardTypeList,
        [Out] ushort[] cardIdList);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_total_axes(ushort cardNo, ref uint totalAxes);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_get_total_axes(ushort cardNo, ref uint totalAxes);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_total_ionum(ushort cardNo, ref ushort totalInputs, ref ushort totalOutputs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_get_total_ionum(ushort cardNo, ref ushort totalInputs, ref ushort totalOutputs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_total_adcnum(ushort cardNo, ref ushort totalInputs, ref ushort totalOutputs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_get_errcode(ushort cardNo, ushort channel, ref ushort errorCode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_clear_errcode(ushort cardNo, ushort channel);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_set_axis_enable(ushort cardNo, ushort axis);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_set_axis_disable(ushort cardNo, ushort axis);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_get_axis_state_machine(ushort cardNo, ushort axis, ref ushort stateMachine);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_get_axis_errcode(ushort cardNo, ushort axis, ref ushort errorCode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_clear_axis_errcode(ushort cardNo, ushort axis);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_set_home_profile(
        ushort cardNo,
        ushort axis,
        ushort homeMode,
        double lowVelocity,
        double highVelocity,
        double accelerationSeconds,
        double decelerationSeconds,
        double offsetPosition);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_home_move(ushort cardNo, ushort axis);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_home_result(ushort cardNo, ushort axis, ref ushort state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_set_profile_unit(
        ushort cardNo,
        ushort axis,
        double minimumVelocity,
        double maximumVelocity,
        double accelerationSeconds,
        double decelerationSeconds,
        double stopVelocity);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_set_s_profile(ushort cardNo, ushort axis, ushort mode, double timeSeconds);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_set_dec_stop_time(ushort cardNo, ushort axis, double stopTimeSeconds);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_pmove_unit(ushort cardNo, ushort axis, double distance, ushort positionMode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_sync_pmove_unit(
        ushort cardNo,
        ushort axisNum,
        ushort[] axisList,
        double[] distanceList,
        ushort[] positionModeList);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_vmove(ushort cardNo, ushort axis, ushort direction);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_position_unit(ushort cardNo, ushort axis, ref double position);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_encoder_unit(ushort cardNo, ushort axis, ref double position);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_target_position_unit(ushort cardNo, ushort axis, ref double position);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_read_current_speed_unit(ushort cardNo, ushort axis, ref double speed);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_axis_run_mode(ushort cardNo, ushort axis, ref ushort runMode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_check_done(ushort cardNo, ushort axis);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_axis_io_status_ex(ushort cardNo, ushort axis, ref uint state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_stop_reason(ushort cardNo, ushort axis, ref int stopReason);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_clear_stop_reason(ushort cardNo, ushort axis);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_stop(ushort cardNo, ushort axis, ushort stopMode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_emg_stop(ushort cardNo);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_read_inport_ex(ushort cardNo, ushort portNo, ref uint state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_read_outport_ex(ushort cardNo, ushort portNo, ref uint state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_write_outbit(ushort cardNo, ushort bitNo, ushort enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_read_inport_extern(
        ushort cardNo,
        ushort channel,
        ushort nodeId,
        ushort portNo,
        ref uint state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_read_outport_extern(
        ushort cardNo,
        ushort channel,
        ushort nodeId,
        ushort portNo,
        ref uint state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short nmc_write_outbit_extern(
        ushort cardNo,
        ushort channel,
        ushort nodeId,
        ushort bitNo,
        ushort value);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_ad_input(ushort cardNo, ushort channel, ref double value);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_get_da_output(ushort cardNo, ushort channel, ref double value);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern short dmc_set_da_output(ushort cardNo, ushort channel, double value);
}
