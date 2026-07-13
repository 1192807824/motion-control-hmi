using System.IO;
using System.Windows;

namespace ControlHub;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string VisionMasterX86RuntimeDirectory = @"D:\VisonMaster\VisionMaster4.4.0\Applications\PublicFile\x86";

    public App()
    {
        PrefixProcessPath(VisionMasterX86RuntimeDirectory);
    }

    private static void PrefixProcessPath(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var existingEntries = currentPath.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var remainingPath = string.Join(
            ';',
            existingEntries.Where(entry => !PathsEqual(entry, directory)));
        Environment.SetEnvironmentVariable(
            "PATH",
            string.IsNullOrEmpty(remainingPath) ? directory : $"{directory};{remainingPath}");
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
