using System.Windows;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private void FirstSetBreakVacuumEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPresetPositions || FirstSetBreakVacuumEnabledCheckBox is null) return;
        _homeSettings.FirstSetBreakVacuumEnabled = FirstSetBreakVacuumEnabledCheckBox.IsChecked == true;
        try { _homeSettingsStore.Save(_homeSettings); }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存第一套破真空开关失败：{exception.Message}", false);
        }
        UpdateHomeCommandState();
    }
}
