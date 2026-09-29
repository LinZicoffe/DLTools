using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using 烧录上位机程序.Services.JLink;

namespace 烧录上位机程序.Pages;

public partial class FlashPage : BasePage
{
    private FlashWorker? _worker;

    public FlashPage()
    {
        InitializeComponent();
        _worker = new FlashWorker();
        _worker.FlashLog += OnFlashLog;
        _worker.FlashProgress += OnFlashProgress;
        _worker.FlashStageChanged += OnFlashStageChanged;
        _worker.FlashFinished += OnFlashFinished;
        DataContext = _worker;
    }

    /// <summary>烧录是否进行中（供主窗口关窗确认流程使用）。</summary>
    public bool IsFlashBusy => _worker?.IsBusy ?? false;

    /// <summary>当前烧录任务，供关窗流程等待安全退出。</summary>
    public Task? FlashTask => _worker?.CurrentTask;

    /// <summary>请求停止当前烧录（在段/校验块边界生效）。</summary>
    public void RequestStopFlash() => _worker?.StopFlash();

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "固件文件 (*.axf *.elf *.hex *.bin)|*.axf;*.elf;*.hex;*.bin|所有文件 (*.*)|*.*",
            FilterIndex = 1
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
        {
            FilePathBox.Text = dialog.FileName;
        }
    }

    private void StartFlash_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DeviceNameBox.Text))
        {
            MessageBox.Show("请填写目标设备名（如 STM32H750VB）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(FilePathBox.Text) || !File.Exists(FilePathBox.Text))
        {
            MessageBox.Show("请先选择有效的 .axf/.elf/.hex/.bin 文件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var p = new FlashParams
        {
            DeviceName = DeviceNameBox.Text.Trim(),
            Interface = InterfaceCombo.SelectedItem is ComboBoxItem cbi && cbi.Content.ToString() == "JTAG" ? "JTAG" : "SWD",
            SpeedKhz = int.TryParse(SpeedBox.Text, out int spd) ? spd : 4000,
            FilePath = FilePathBox.Text.Trim(),
            FileFormat = Path.GetExtension(FilePathBox.Text).ToLower().TrimStart('.'),
            BinStartAddr = 0,
            EraseMode = EraseChip.IsChecked == true ? JLinkBackend.EraseModeChip : JLinkBackend.EraseModeSector,
            PostAction = GetPostAction(),
            Serial = SerialBox.Text.Trim(),
            EraseOnly = false
        };

        _worker!.StartFlash(p);
        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
    }

    private void StopFlash_Click(object sender, RoutedEventArgs e)
    {
        // 只请求取消并禁用自身；StartBtn 由 OnFlashFinished 统一恢复，
        // 避免原生烧录任务未退出时再次开始造成并发访问 DLL
        StopBtn.IsEnabled = false;
        _worker?.StopFlash();
    }

    private void OnFlashLog(string level, string msg)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}\n");
            LogBox.ScrollToEnd();
        });
    }

    private void OnFlashProgress(int current, int total)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = current;
        });
    }

    private void OnFlashStageChanged(string stage)
    {
        Dispatcher.Invoke(() =>
        {
            // 过程中状态回到中性色；成功/失败的终态色由 OnFlashFinished 决定
            StageText.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
            StageText.Text = stage switch
            {
                "connect" => "连接中...",
                "erase" => "擦除中...",
                "program" => "烧录中...",
                "verify" => "校验中...",
                "reset" => "复位中...",
                "disconnect" => "断开中...",
                _ => stage
            };
        });
    }

    private void OnFlashFinished(bool success, string summary)
    {
        Dispatcher.Invoke(() =>
        {
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            StageText.Text = success ? "完成" : "失败";
            StageText.Foreground = new SolidColorBrush(
                success ? Color.FromRgb(0x0E, 0x70, 0x0E) : Color.FromRgb(0xC5, 0x0F, 0x1F));
            if (success)
            {
                LogBox.AppendText($"\n*** 烧录成功: {summary} ***\n");
            }
            else
            {
                LogBox.AppendText($"\n*** 烧录失败: {summary} ***\n");
            }
            LogBox.ScrollToEnd();
        });
    }

    private string GetPostAction()
    {
        if (ResetHaltRadio.IsChecked == true) return "halt";
        if (ResetRunRadio.IsChecked == true) return "reset_run";
        return "none";
    }
}
