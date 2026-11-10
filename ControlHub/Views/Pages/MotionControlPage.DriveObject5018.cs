using ControlHub.Services.Motion;

namespace ControlHub.Views.Pages;

public partial class MotionControlPage
{
    public Task<IReadOnlyList<Drive5018AxisResult>> WriteAllZAxes5018Async(ushort value)
    {
        if (_closed || !_motionCard.IsOpen) throw new InvalidOperationException("运动控制卡尚未连接。");
        if (_motionOptions.SimulationMode) throw new InvalidOperationException("模拟模式不能写入真实驱动器0x5018。");
        if (_homeSequenceCancellation is not null || IsAnyMotionWorkflowActive())
            throw new InvalidOperationException("请等待当前运动或回零流程结束后写入0x5018。");
        ThrowIfExternalEmergencyStopActive();
        return Task.Run(() => DriveObject5018.WriteAll(value, _motionCard.WriteZAxis5018));
    }
}
