using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using 烧录上位机程序.Models;
using 烧录上位机程序.Services.JLink;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// 固件烧录流程编排器。
///
/// 流程：connect → [erase] → program → [verify] → [reset] → close。
/// 在 Task.Run 中运行，通过事件通知 UI 线程更新。
/// </summary>
public partial class FlashWorker : ObservableObject
{
    private CancellationTokenSource? _cts;

    public FlashWorker()
    {
        Logs = new();
    }

    #region Observable Properties

    [ObservableProperty]
    private bool _isBusy = false;

    [ObservableProperty]
    private string _stageText = "就绪";

    [ObservableProperty]
    private int _progressValue = 0;

    [ObservableProperty]
    private int _progressMax = 100;

    public ObservableCollection<string> Logs { get; }

    #endregion

    #region Events

    public event Action<string, string>? FlashLog;
    public event Action<int, int>? FlashProgress;
    public event Action<string>? FlashStageChanged;
    public event Action<bool, string>? FlashFinished;

    #endregion

    #region Public Methods

    public void StartFlash(FlashParams p)
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressValue = 0;
        Logs.Clear();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunFlashAsync(p, _cts.Token));
    }

    public void StopFlash()
    {
        // 只请求取消；IsBusy 由任务 finally 复位，完成事件由 catch(OperationCanceledException) 统一上报。
        // 若在此立即放开 IsBusy，原生烧录任务未退出时就能再启动第二个任务并发进入同一 DLL（非线程安全）。
        _cts?.Cancel();
        StageText = "正在取消...";
    }

    #endregion

    #region Flash Workflow

    private async Task RunFlashAsync(FlashParams p, CancellationToken ct)
    {
        JLinkBackend? backend = null;
        try
        {
            var loader = DllLoader.Current;
            backend = new JLinkBackend(loader, (level, msg) =>
            {
                FlashLog?.Invoke(level, msg);
                AddLog($"[{level}] {msg}");
            });

            var probeParams = new ProbeParams
            {
                DeviceName = p.DeviceName,
                Interface = p.Interface,
                SpeedKhz = p.SpeedKhz,
                FilePath = p.FilePath,
                FileFormat = p.FileFormat,
                BinStartAddr = p.BinStartAddr,
                EraseMode = p.EraseMode,
                PostAction = p.PostAction,
                Serial = p.Serial,
                RemoteAddr = p.RemoteAddr,
                EraseOnly = p.EraseOnly
            };

            // Stage: Connect
            FlashStageChanged?.Invoke("connect");
            SetStage("连接中...");
            backend.Connect(probeParams);
            AddLog("[info] 目标已连接: " + p.DeviceName);
            ct.ThrowIfCancellationRequested();

            if (p.EraseOnly)
            {
                FlashStageChanged?.Invoke("erase");
                SetStage("擦除中...");
                backend.Erase(JLinkBackend.EraseModeChip);
                AddLog("[info] 整片擦除完成");
                backend.Close();
                FlashFinished?.Invoke(true, "整片擦除完成");
                return;
            }

            // Erase
            if (p.EraseMode == JLinkBackend.EraseModeChip)
            {
                FlashStageChanged?.Invoke("erase");
                SetStage("擦除中...");
                backend.Erase(JLinkBackend.EraseModeChip);
                AddLog("[info] 芯片擦除完成");
            }
            ct.ThrowIfCancellationRequested(); // 整片擦除（不可中断）完成后立即响应取消

            // Program
            FlashStageChanged?.Invoke("program");
            SetStage("烧录中...");

            void OnProgress(int current, int total)
            {
                FlashProgress?.Invoke(current, total);
                ProgressValue = current;
            }

            backend.Program(p.FilePath, p.FileFormat, p.BinStartAddr, OnProgress, ct);
            AddLog("[info] 烧录完成");

            // Verify（必选：写完必须读回比对。DLL 层不报错不代表数据真的落进了 Flash，
            // 缺了这一步就会出现"烧录失败但日志显示成功"）
            FlashStageChanged?.Invoke("verify");
            SetStage("校验中...");
            backend.Verify(p.FilePath, p.FileFormat, p.BinStartAddr, ct);
            AddLog("[info] 校验通过");

            // Reset
            if (p.PostAction != JLinkBackend.PostActionNone)
            {
                ct.ThrowIfCancellationRequested(); // 复位前响应取消（烧录已完整，跳过复位）
                FlashStageChanged?.Invoke("reset");
                SetStage("复位中...");
                bool halt = p.PostAction == JLinkBackend.PostActionHalt;
                bool run = p.PostAction == JLinkBackend.PostActionResetRun;
                backend.Reset(halt, run);
            }

            // Disconnect
            FlashStageChanged?.Invoke("disconnect");
            backend.Close();

            SetStage("完成");
            FlashFinished?.Invoke(true, "烧录成功");
        }
        catch (OperationCanceledException)
        {
            SetStage("已取消");
            AddLog("[warn] 烧录已取消");
            backend?.Close();
            FlashFinished?.Invoke(false, "已取消");
        }
        catch (Exception ex)
        {
            AddLog($"[error] {ex.GetType().Name}: {ex.Message}");
            backend?.Close();
            FlashFinished?.Invoke(false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    private void SetStage(string text)
    {
        StageText = text;
        FlashStageChanged?.Invoke(text);
    }

    private void AddLog(string msg)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        Logs.Add($"[{time}] {msg}");
    }
}

/// <summary>
/// 烧录参数。
/// </summary>
public class FlashParams
{
    public string FilePath { get; set; } = "";
    public string FileFormat { get; set; } = "hex";
    public int BinStartAddr { get; set; } = 0;
    public string DeviceName { get; set; } = "";
    public string Interface { get; set; } = "SWD";
    public int SpeedKhz { get; set; } = 4000;
    public string EraseMode { get; set; } = "sector";
    public string PostAction { get; set; } = "reset_run";
    public string Serial { get; set; } = "";
    public string RemoteAddr { get; set; } = "";
    public bool EraseOnly { get; set; } = false;
}
