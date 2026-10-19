using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using 烧录上位机程序.Models;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.JLink;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// 固件烧录流程编排器。
///
/// 流程：connect → [erase] → program → [verify] → [reset] → close。
/// 在 Task.Run 中运行，通过事件通知 UI 线程更新。
/// 阶段事件只发送语义代码（connect/erase/program/verify/reset/disconnect/cancel），
/// 显示文案由 UI 层经 I18nService 本地化。
/// </summary>
public partial class FlashWorker : ObservableObject
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _currentTask;

    /// <summary>当前烧录任务；关窗流程等待其安全退出，避免进程在原生调用中途被杀。</summary>
    public Task? CurrentTask => _currentTask;

    #region Observable Properties

    [ObservableProperty]
    private bool _isBusy = false;

    #endregion

    #region Events

    public event Action<string, string>? FlashLog;
    public event Action<int, int>? FlashProgress;
    public event Action<string>? FlashStageChanged;
    public event Action<bool, string>? FlashFinished;

    #endregion

    #region Public Methods

    /// <summary>
    /// 启动烧录任务。返回 false 表示已有任务在跑被拒绝（调用方据此不翻转按钮状态，
    /// 否则竞态下按钮会被永久卡死：早退后没有任何事件再来恢复按钮）。
    /// </summary>
    public bool StartFlash(FlashParams p)
    {
        lock (_gate)
        {
            if (_currentTask is { IsCompleted: false }) return false;
            var cancellation = new CancellationTokenSource();
            IsBusy = true;
            if (!BackgroundTaskHost.Shared.TryStart(BackgroundOperationKind.Flashing, hostToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken, cancellation.Token);
                    try { RunFlash(p, linked.Token); }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_cts, cancellation)) _cts = null;
                            cancellation.Dispose();
                        }
                    }
                    return Task.CompletedTask;
                }, out var task))
            {
                cancellation.Dispose();
                IsBusy = false;
                return false;
            }
            _cts = cancellation;
            _currentTask = task;
            return true;
        }
    }

    public void StopFlash()
    {
        // 只请求取消；IsBusy 由任务 finally 复位，完成事件由 catch(OperationCanceledException) 统一上报。
        // 若在此立即放开 IsBusy，原生烧录任务未退出时就能再启动第二个任务并发进入同一 DLL（非线程安全）。
        lock (_gate) _cts?.Cancel();
        // 取消只在段/4096 块边界真正生效，先发 cancel 阶段让页面立即显示"正在取消…"
        FlashStageChanged?.Invoke("cancel");
    }

    #endregion

    #region Flash Workflow

    private void RunFlash(FlashParams p, CancellationToken ct)
    {
        JLinkBackend? backend = null;
        DllLoader? loader = null;
        bool success = false;
        string summary = "";
        try
        {
            ct.ThrowIfCancellationRequested();
            // A failed load affects this attempt only; the next flash can retry after driver installation.
            loader = new DllLoader();
            FlashLog?.Invoke("info", $"JLink_x64.dll 版本: {loader.GetDLLVersion()}");
            backend = new JLinkBackend(loader, (level, msg) =>
            {
                FlashLog?.Invoke(level, msg);
            });

            var probeParams = new ProbeParams
            {
                DeviceName = p.DeviceName,
                Interface = p.Interface,
                SpeedKhz = p.SpeedKhz,
                Serial = p.Serial,
                RemoteAddr = p.RemoteAddr
            };

            // Stage: Connect
            FlashStageChanged?.Invoke("connect");
            backend.Connect(probeParams);
            FlashLog?.Invoke("info", string.Format(I18nService.GetString("目标已连接: {0}"), p.DeviceName));
            ct.ThrowIfCancellationRequested();

            // Erase
            if (p.EraseMode == JLinkBackend.EraseModeChip)
            {
                FlashStageChanged?.Invoke("erase");
                backend.Erase(JLinkBackend.EraseModeChip);
                FlashLog?.Invoke("info", I18nService.GetString("芯片擦除完成"));
            }
            ct.ThrowIfCancellationRequested(); // 整片擦除（不可中断）完成后立即响应取消

            // Program
            FlashStageChanged?.Invoke("program");
            backend.Program(p.FilePath, p.FileFormat, p.BinStartAddr, (current, total) =>
            {
                FlashProgress?.Invoke(current, total);
            }, ct);
            FlashLog?.Invoke("info", I18nService.GetString("烧录完成"));

            // Verify（必选：写完必须读回比对。DLL 层不报错不代表数据真的落进了 Flash，
            // 缺了这一步就会出现"烧录失败但日志显示成功"）
            FlashStageChanged?.Invoke("verify");
            backend.Verify(p.FilePath, p.FileFormat, p.BinStartAddr, ct);
            FlashLog?.Invoke("info", I18nService.GetString("校验通过"));

            // Reset
            if (p.PostAction != JLinkBackend.PostActionNone)
            {
                ct.ThrowIfCancellationRequested(); // 复位前响应取消（烧录已完整，跳过复位）
                FlashStageChanged?.Invoke("reset");
                bool halt = p.PostAction == JLinkBackend.PostActionHalt;
                bool run = p.PostAction == JLinkBackend.PostActionResetRun;
                backend.Reset(halt, run);
            }

            // Disconnect
            FlashStageChanged?.Invoke("disconnect");
            success = true;
            summary = I18nService.GetString("烧录成功");
        }
        catch (OperationCanceledException)
        {
            summary = I18nService.GetString("已取消");
        }
        catch (Exception ex)
        {
            summary = ex.Message;
        }
        finally
        {
            // Cleanup must not replace the original failure or suppress the completion notification.
            try { backend?.Close(); }
            catch (Exception ex) { try { LoggerService.Error($"关闭 J-Link 会话失败: {ex.Message}"); } catch { } }
            try { loader?.Dispose(); }
            catch (Exception ex) { try { LoggerService.Error($"释放 J-Link DLL 失败: {ex.Message}"); } catch { } }
            finally
            {
                IsBusy = false;
                FlashFinished?.Invoke(success, summary);
            }
        }
    }

    #endregion
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
}
