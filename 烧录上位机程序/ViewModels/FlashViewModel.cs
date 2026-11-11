using System;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using 烧录上位机程序.Models;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.JLink;

namespace 烧录上位机程序.ViewModels;

/// <summary>烧录终态的视觉样式（StageText 颜色由 View 的 DataTrigger 呈现）。</summary>
public enum FlashStageVisual
{
    Neutral,
    Success,
    Failure
}

/// <summary>校验失败时需要聚焦的输入框（View 据此把焦点移到出错控件）。</summary>
public enum FlashInputField
{
    None,
    DeviceName,
    FilePath,
    BinAddr
}

/// <summary>
/// 固件烧录页 ViewModel：承载输入状态、输入校验、FlashParams 构造与按钮/阶段状态机。
/// 文件浏览与错误提示对话框留在 View；其余逻辑全部可在此单测。
/// </summary>
public partial class FlashViewModel : ObservableObject
{
    private readonly FlashWorker _worker;
    private readonly Dispatcher _dispatcher;

    public FlashViewModel(FlashWorker? worker = null, Dispatcher? dispatcher = null)
    {
        _worker = worker ?? new();
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _worker.FlashLog += OnFlashLog;
        _worker.FlashProgress += OnFlashProgress;
        _worker.FlashStageChanged += OnStageChanged;
        _worker.FlashFinished += OnFinished;
    }

    #region 输入状态（TwoWay 绑定）

    [ObservableProperty] private string _deviceName = "STM32H750VB";
    [ObservableProperty] private int _interfaceIndex;          // 0=SWD, 1=JTAG
    [ObservableProperty] private string _speedText = "4000";
    [ObservableProperty] private string _serial = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _binAddrText = "";
    [ObservableProperty] private bool _isChipErase;
    [ObservableProperty] private bool _isPostResetRun = true;
    [ObservableProperty] private bool _isPostHalt;
    [ObservableProperty] private bool _isPostNone;

    #endregion

    #region 输出状态（OneWay 绑定）

    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private string _stageText = I18nService.GetString("就绪");
    [ObservableProperty] private FlashStageVisual _stageVisual = FlashStageVisual.Neutral;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private string _logText = "";

    #endregion

    // RadioButton 的 GroupName 已在 UI 层互斥；程序化赋值时同样保持互斥（测试直接设属性）。
    partial void OnIsPostResetRunChanged(bool value) { if (value) { IsPostHalt = false; IsPostNone = false; } }
    partial void OnIsPostHaltChanged(bool value) { if (value) { IsPostResetRun = false; IsPostNone = false; } }
    partial void OnIsPostNoneChanged(bool value) { if (value) { IsPostResetRun = false; IsPostHalt = false; } }

    #region 校验与参数构造

    /// <summary>解析地址输入：支持 0x 前缀十六进制与十进制；空串/非法输入返回 false。</summary>
    public static bool TryParseAddress(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = NumberStyles.HexNumber;
        }
        return int.TryParse(text, style, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 校验输入并构造烧录参数。失败时 error 为用户可见文案（I18n key），field 指示出错控件。
    /// </summary>
    public bool TryBuildParams(out FlashParams? parameters, out string error, out FlashInputField field)
    {
        parameters = null;
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            error = "请填写目标设备名（如 STM32H750VB）";
            field = FlashInputField.DeviceName;
            return false;
        }
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            error = "请先选择有效的 .axf/.elf/.hex/.bin 文件";
            field = FlashInputField.FilePath;
            return false;
        }

        // Invariant：土耳其语等区域下 ToLower 会把 'I' 映射为 'ı'，导致 .BIN 识别失败
        var fileFormat = Path.GetExtension(FilePath).ToLowerInvariant().TrimStart('.');

        // .bin 文件不带地址信息，起始地址必须由用户提供（此前硬编码 0，
        // 而 STM32 等 Flash 基址在 0x08000000，向 0 地址烧录必然错误）
        int binStartAddr = 0;
        if (fileFormat == FlashFileParser.FormatBin && !TryParseAddress(BinAddrText, out binStartAddr))
        {
            error = "请填写有效的 Bin 起始地址（如 0x08000000）";
            field = FlashInputField.BinAddr;
            return false;
        }

        parameters = new FlashParams
        {
            DeviceName = DeviceName.Trim(),
            Interface = InterfaceIndex == 1 ? "JTAG" : "SWD",
            SpeedKhz = int.TryParse(SpeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed) ? speed : 4000,
            FilePath = FilePath.Trim(),
            FileFormat = fileFormat,
            BinStartAddr = binStartAddr,
            EraseMode = IsChipErase ? JLinkBackend.EraseModeChip : JLinkBackend.EraseModeSector,
            PostAction = IsPostHalt ? JLinkBackend.PostActionHalt
                : IsPostNone ? JLinkBackend.PostActionNone
                : JLinkBackend.PostActionResetRun,
            Serial = Serial.Trim(),
        };
        error = "";
        field = FlashInputField.None;
        return true;
    }

    #endregion

    #region 任务控制

    /// <summary>启动烧录；返回 false 表示宿主忙被拒绝（按钮状态不翻转，由调用方提示）。</summary>
    public bool StartFlash(FlashParams parameters)
    {
        if (!_worker.StartFlash(parameters)) return false;
        CanStart = false;
        CanStop = true;
        return true;
    }

    /// <summary>请求停止：先禁用停止按钮防重复点击；开始按钮由完成事件统一恢复，
    /// 避免原生烧录任务未退出时再次开始造成并发访问 DLL。</summary>
    public void StopFlash()
    {
        CanStop = false;
        _worker.StopFlash();
    }

    #endregion

    #region Worker 事件 → UI 状态

    private void OnFlashLog(string level, string msg) =>
        RunOnUi(() => AppendLog($"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}"));

    private void OnFlashProgress(int current, int total) => RunOnUi(() => ProgressValue = current);

    private void OnStageChanged(string stage) => RunOnUi(() =>
    {
        // 过程中状态回到中性色；成功/失败的终态色由 OnFinished 决定
        StageVisual = FlashStageVisual.Neutral;
        StageText = stage switch
        {
            "connect" => I18nService.GetString("连接中…"),
            "erase" => I18nService.GetString("擦除中…"),
            "program" => I18nService.GetString("烧录中…"),
            "verify" => I18nService.GetString("校验中…"),
            "reset" => I18nService.GetString("复位中…"),
            "disconnect" => I18nService.GetString("断开中…"),
            "cancel" => I18nService.GetString("正在取消…"),
            _ => stage,
        };
    });

    private void OnFinished(bool success, string summary) => RunOnUi(() =>
    {
        CanStart = true;
        CanStop = false;
        StageText = success ? I18nService.GetString("完成") : I18nService.GetString("失败");
        StageVisual = success ? FlashStageVisual.Success : FlashStageVisual.Failure;
        AppendLog(string.Format(I18nService.GetString(success ? "*** 烧录成功: {0} ***" : "*** 烧录失败: {0} ***"), summary));
    });

    private void AppendLog(string line) => LogText = LogText.Length == 0 ? line : LogText + "\n" + line;

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.InvokeAsync(action);
    }

    #endregion
}
