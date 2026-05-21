namespace ControlHub.Services;

public interface IMotionCard
{
    bool Open();
    void Close();
    void ServoOn(int axisNo, bool enabled);
    void Home(int axisNo);
    void Jog(int axisNo, double velocity);
    void Stop(int axisNo);
    void EmergencyStop();
}
