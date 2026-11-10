using System.Windows.Controls;
using ControlHub.Services.Persistence;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    // 生产开始时锁定，当前批次不随其他参数保存或暂停恢复而改变。
    private int _productionPhotoSettleMilliseconds = HomePageSettings.DefaultProductionPhotoSettleMilliseconds;

    private int ReadProductionPhotoSettleMilliseconds() =>
        ParseMilliseconds(ProductionPhotoSettleTextBox.Text, "震动后拍照等待");

    private void ProductionPhotoSettleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveProductionPhotoSettleFromInput();
        if (!_loadingPresetPositions) UpdateHomeCommandState();
    }

    private void SaveProductionPhotoSettleFromInput(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions || ProductionPhotoSettleTextBox is null) return;
        if (!TryParseMilliseconds(ProductionPhotoSettleTextBox.Text, out var milliseconds))
        {
            if (throwOnInvalid) _ = ReadProductionPhotoSettleMilliseconds();
            return;
        }
        _homeSettings.ProductionPhotoSettleMilliseconds = milliseconds;
        try { _homeSettingsStore.Save(_homeSettings); }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存震动后拍照等待失败：{exception.Message}", false);
        }
    }
}
