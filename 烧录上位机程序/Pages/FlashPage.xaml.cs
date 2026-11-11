using System.Windows;
using System.Windows.Controls;
using 烧录上位机程序.Services;
using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序.Pages;

/// <summary>
/// 烧录页 View：仅承载文件对话框、校验错误提示与日志滚动；
/// 输入校验、FlashParams 构造与按钮/阶段状态机全部在 FlashViewModel（可单测）。
/// </summary>
public partial class FlashPage : BasePage
{
    private readonly FlashViewModel _viewModel;

    public FlashPage()
    {
        InitializeComponent();
        DataContext = _viewModel = new FlashViewModel();
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "固件文件 (*.axf *.elf *.hex *.bin)|*.axf;*.elf;*.hex;*.bin|所有文件 (*.*)|*.*",
            FilterIndex = 1
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
            _viewModel.FilePath = dialog.FileName;
    }

    private void StartFlash_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryBuildParams(out var parameters, out var error, out var field))
        {
            if (field == FlashInputField.BinAddr) BinAddrBox.Focus();
            MessageBox.Show(I18nService.GetString(error), I18nService.GetString("提示"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 仅在任务真正启动时翻转按钮；竞态下 StartFlash 拒绝重复启动时
        // 不动按钮（否则再无事件恢复，按钮会永久卡死）
        if (!_viewModel.StartFlash(parameters!))
        {
            MessageBox.Show("另一个后台任务正在运行，请等待完成后再开始烧录。", "任务进行中",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void StopFlash_Click(object sender, RoutedEventArgs e) => _viewModel.StopFlash();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
