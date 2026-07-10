namespace ControlHub.Services.Motion;

public static class MotionCardFactory
{
    public static IMotionCard Create(MotionCardOptions options)
    {
        return options.SimulationMode
            ? new SimulatedMotionCard(options)
            : new LeisaiMotionCard(options);
    }
}
