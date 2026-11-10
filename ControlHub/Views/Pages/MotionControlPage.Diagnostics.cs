using System.Globalization;

namespace ControlHub.Views.Pages;

public partial class MotionControlPage
{
    internal string CaptureProductionDiagnosticState()
    {
        Dispatcher.VerifyAccess();
        // Cached feedback only: no extra motion-card reads or commands.
        return string.Join(";", (Axes ?? []).Where(axis => axis.IsAvailable).Select(axis =>
            string.Create(CultureInfo.InvariantCulture,
                $"axis={axis.HardwareAxisNo},pos={axis.Position:0.###},target={axis.Target:0.###},moving={axis.IsMoving},alarm={axis.Alarm},healthy={axis.StatusReadHealthy}")));
    }
}
