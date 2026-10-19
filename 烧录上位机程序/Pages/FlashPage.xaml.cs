using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using 烧录上位机程序.Services;
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
            MessageBox.Show(I18nService.GetString("请填写目标设备名（如 STM32H750VB）"),
                I18nService.GetString("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(FilePathBox.Text) || !File.Exists(FilePathBox.Text))
        {
            MessageBox.Show(I18nService.GetString("请先选择有效的 .axf/.elf/.hex/.bin 文件"),
                I18nService.GetString("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Invariant：土耳其语等区域下 ToLower 会把 'I' 映射为 'ı'，导致 .BIN 识别失败
        var fileFormat = Path.GetExtension(FilePathBox.Text).ToLowerInvariant().TrimStart('.');

        // .bin 文件不带地址信息，起始地址必须由用户提供（此前硬编码 0，
        // 而 STM32 等 Flash 基址在 0x08000000，向 0 地址烧录必然错误）
        int binStartAddr = 0;
        if (fileFormat == "bin")
        {
            if (!TryParseAddress(BinAddrBox.Text, out binStartAddr))
            {
                MessageBox.Show(I18nService.GetString("请填写有效的 Bin 起始地址（如 0x08000000）"),
                    I18nService.GetString("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                BinAddrBox.Focus();
                return;
            }
        }

        var p = new FlashParams
        {
            DeviceName = DeviceNameBox.Text.Trim(),
            Interface = InterfaceCombo.SelectedItem is ComboBoxItem cbi && cbi.Content.ToString() == "JTAG" ? "JTAG" : "SWD",
            SpeedKhz = int.TryParse(SpeedBox.Text, out int spd) ? spd : 4000,
            FilePath = FilePathBox.Text.Trim(),
            FileFormat = fileFormat,
            BinStartAddr = binStartAddr,
            EraseMode = EraseChip.IsChecked == true ? JLinkBackend.EraseModeChip : JLinkBackend.EraseModeSector,
            PostAction = GetPostAction(),
            Serial = SerialBox.Text.Trim()
        };

        // 仅在任务真正启动时翻转按钮；竞态下 StartFlash 拒绝重复启动时
        // 不动按钮（否则再无事件恢复，按钮会永久卡死）
        if (!_worker!.StartFlash(p))
        {
            if (!_worker.IsBusy)
                MessageBox.Show("另一个后台任务正在运行，请等待完成后再开始烧录。", "任务进行中",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
                "connect" => I18nService.GetString("连接中…"),
                "erase" => I18nService.GetString("擦除中…"),
                "program" => I18nService.GetString("烧录中…"),
                "verify" => I18nService.GetString("校验中…"),
                "reset" => I18nService.GetString("复位中…"),
                "disconnect" => I18nService.GetString("断开中…"),
                "cancel" => I18nService.GetString("正在取消…"),
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
            StageText.Text = success ? I18nService.GetString("完成") : I18nService.GetString("失败");
            StageText.Foreground = new SolidColorBrush(
                success ? Color.FromRgb(0x0E, 0x70, 0x0E) : Color.FromRgb(0xC5, 0x0F, 0x1F));
            LogBox.AppendText($"\n{string.Format(I18nService.GetString(success ? "*** 烧录成功: {0} ***" : "*** 烧录失败: {0} ***"), summary)}\n");
            LogBox.ScrollToEnd();
        });
    }

    private string GetPostAction()
    {
        if (ResetHaltRadio.IsChecked == true) return "halt";
        if (ResetRunRadio.IsChecked == true) return "reset_run";
        return "none";
    }

    /// <summary>解析地址输入：支持 0x 前缀十六进制、裸十六进制与十进制。</summary>
    private static bool TryParseAddress(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var style = System.Globalization.NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = System.Globalization.NumberStyles.HexNumber;
        }
        return int.TryParse(text, style, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
