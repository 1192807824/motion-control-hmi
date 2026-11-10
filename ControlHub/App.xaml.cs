using System.Text;
using System.Windows;

namespace ControlHub;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Services.Persistence.AlarmHistory.Initialize();
        DispatcherUnhandledException += (_, e) =>
            Services.Persistence.AlarmHistory.Record("系统", "UNHANDLED-UI", e.Exception.ToString());
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Services.Persistence.AlarmHistory.Record("系统", "UNHANDLED", e.ExceptionObject.ToString() ?? "未知异常");
            Services.Persistence.AlarmHistory.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        };
        Exit += (_, _) => Services.Persistence.AlarmHistory.FlushAsync().Wait(TimeSpan.FromSeconds(2));
    }
}
