using System;
using System.Linq;
using System.Threading;
using 烧录上位机程序.Models;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// J-Link 烧录后端：通过 DllLoader 直接调用 JLink_x64.dll。
///
/// 调用序列与 pylink-square 1.6.0 逐项对齐（Python 参考 src/core/probe/jlink_backend.py）：
/// - 连接：open → close → open(serial) → set_tif → set_speed → connect
///   （pylink open() 内部自带前置 close；"读 SN 再重开"双开来自 Python 参考实现）
/// - set_tif：SWD/JTAG 二选一（pylink enums.JLinkInterfaces：JTAG=0, SWD=1）
/// - 编程：每段 halt(容忍失败) → BeginDownload(0) → WriteMem → EndDownload
/// - 校验：ReadMemEx 按 4096 字节分块比对（Python _verify_range CHUNK=4096）
/// - 整片擦除：halt(容忍失败) → JLINK_EraseChip
/// - 复位：SetResetDelay(0) → Reset()；halt 模式追加 Halt()，run 模式追加 Restart()
/// </summary>
public sealed class JLinkBackend : IDisposable
{
    private const int VerifyChunk = 4096; // Python _verify_range 的分块大小

    private readonly DllLoader _dll;
    private readonly Action<string, string> _log;
    private bool _connected = false;

    public JLinkBackend(DllLoader dll, Action<string, string> log)
    {
        _dll = dll;
        _log = log;
    }

    public bool IsConnected => _connected;
    public string ConnectedSerial { get; private set; } = "";

    // pylink enums.JLinkInterfaces：JTAG=0, SWD=1
    public const int JLINK_IF_JTAG = 0;
    public const int JLINK_IF_SWD = 1;
    public const string EraseModeChip = "chip";
    public const string EraseModeSector = "sector";
    public const string PostActionNone = "none";
    public const string PostActionHalt = "halt";
    public const string PostActionResetRun = "reset_run";
    public const string FormatBin = "bin";
    public const string FormatHex = "hex";
    public const string FormatElf = "elf";

    public void Connect(ProbeParams p)
    {
        string serial = p.Serial;
        string remote = p.RemoteAddr;

        if (!string.IsNullOrEmpty(remote))
        {
            // 远程模式：跳过 USB 枚举与 serial 校验，按 ip:port 双开
            _log("info", $"J-Link 远程模式: {remote}");
            var (ip, port) = SplitRemote(remote);
            if (!_dll.IsTargetConnected())
            {
                _dll.OpenByIp(ip, port);
                ConnectedSerial = _dll.GetSN().ToString();
                _dll.Close();
                _dll.OpenByIp(ip, port);
            }
            _log("info", $"J-Link SN: {ConnectedSerial} (远程 {remote})");
        }
        else
        {
            // 本地 USB 模式：读 SN 再重开的双开协议
            if (!_dll.IsTargetConnected())
            {
                if (int.TryParse(serial, out int sn) && sn > 0)
                {
                    _dll.OpenBySerial(sn);
                }
                else
                {
                    _dll.OpenDefault();
                }
                ConnectedSerial = _dll.GetSN().ToString();
                _dll.Close();
                _dll.OpenBySerial(int.Parse(ConnectedSerial));
            }
            _log("info", $"J-Link SN: {ConnectedSerial}");
        }

        // 仿真器已打开：此后任一步失败，catch 里的 backend.Close() 都能正确关掉句柄
        _connected = true;

        // SWD / JTAG 二选一（pylink set_tif：不可 OR 起来）
        int tif = p.Interface == "SWD" ? JLINK_IF_SWD : JLINK_IF_JTAG;
        _dll.SetTif(tif);
        _dll.SetSpeed(p.SpeedKhz);
        _dll.Connect(p.DeviceName);
        _log("info", $"Target connected: {p.DeviceName}");
    }

    private static (string Ip, int Port) SplitRemote(string remote)
    {
        // Python: addr, port = ip_addr.rsplit(':', 1)；无端口时用 SEGGER 默认 19020
        int idx = remote.LastIndexOf(':');
        if (idx > 0 && int.TryParse(remote[(idx + 1)..], out int port))
            return (remote[..idx], port);
        return (remote, 19020);
    }

    public void Erase(string mode)
    {
        if (mode == EraseModeChip)
        {
            _log("info", "开始整片擦除...");
            TryHalt();
            _dll.EraseChip();
            _log("info", "整片擦除完成");
        }
        // sector 模式：WriteMem 下载事务内由 DLL 按扇区隐式擦除，不显式整片擦
    }

    /// <summary>pylink erase()/flash() 前置 halt，失败容忍（目标可能已停）。</summary>
    private void TryHalt()
    {
        try { _dll.Halt(); }
        catch (JLinkDllException ex) { _log("warn", $"halt: {ex.Message}"); }
    }

    public void Program(string filePath, string fileFormat, int binStartAddr, Action<int, int> onProgress, CancellationToken ct = default)
    {
        if (!_connected)
            throw new InvalidOperationException("未连接");

        var ih = FlashFileParser.ToIntelHex(filePath, binStartAddr);
        var segments = ih.Segments;
        if (!segments.Any())
            throw new InvalidOperationException("固件无可烧录数据段");

        int totalBytes = segments.Sum(s => s.EndExclusive - s.Start);
        if (totalBytes == 0) totalBytes = 1;
        int done = 0;

        foreach (var (start, endExclusive) in segments)
        {
            ct.ThrowIfCancellationRequested(); // 段边界响应取消，避免 DLL 并发访问

            int len = endExclusive - start;
            var data = ih.ToBinArray(start, endExclusive);
            if (data.Length == 0) continue;

            // pylink flash(data, addr)：halt → Begin(0) → WriteMem → End
            TryHalt();
            _dll.BeginDownload(0);
            int written = _dll.WriteMem(start, data);
            _dll.EndDownload(); // 编程失败的权威信号是 EndDownload 的返回码
            // pylink 明确说明 WriteMem 返回值可以不等于写入长度而非错误，此处仅记录
            if (written != data.Length)
                _log("warn", $"WriteMem 返回 {written}/{data.Length} 字节 @ 0x{start:X8}");

            done += len;
            int percent = (int)Math.Min(100, (long)done * 100 / totalBytes); // long：防 >21MB 固件时 int 溢出为负
            onProgress?.Invoke(percent, 100);
            _log("info", $"已烧录 {len} B @ 0x{start:X8}");
        }
    }

    public void Verify(string filePath, string fileFormat, int binStartAddr, CancellationToken ct = default)
    {
        if (!_connected)
            throw new InvalidOperationException("未连接");

        var ih = FlashFileParser.ToIntelHex(filePath, binStartAddr);
        foreach (var (start, endExclusive) in ih.Segments)
        {
            int len = endExclusive - start;
            var expected = ih.ToBinArray(start, endExclusive);
            if (expected.Length == 0) continue;

            // 与 Python _verify_range 一致：4096 字节分块读取比对
            for (int off = 0; off < len; off += VerifyChunk)
            {
                ct.ThrowIfCancellationRequested(); // 块边界响应取消

                int n = Math.Min(VerifyChunk, len - off);
                var chunk = new byte[n];
                int read = _dll.ReadMem(start + off, chunk);
                if (read < n)
                    throw new InvalidOperationException($"回读不完整: {read}/{n} 字节 @ 0x{start + off:X8}");
                for (int i = 0; i < n; i++)
                {
                    if (chunk[i] != expected[off + i])
                        throw new VerifyMismatch(start + off + i, len);
                }
            }
        }
    }

    public void Reset(bool halt, bool run)
    {
        // pylink reset(ms=0)：SetResetDelay(0) + JLINKARM_Reset()（无参数）
        _dll.SetResetDelay(0);
        _dll.Reset();
        if (halt && !run)
        {
            // 容忍失败（与 TryHalt 一致）：复位后核心通常已停在 halt 态，
            // 对已停核心再 Halt 可能返回非 0——不应让烧录+校验已成功的一轮被误报为失败
            TryHalt();
            _log("info", "CPU halted after reset");
        }
        else if (run)
        {
            _dll.Restart(); // pylink restart() = GoEx(0, 0)
            // 复位后如实确认运行状态：仍 halted 说明固件没有真正跑起来，必须让用户看见
            if (_dll.IsCpuHalted())
                _log("warn", "复位后 CPU 仍处于暂停态，固件可能未开始运行");
            else
                _log("info", "CPU running");
        }
    }

    public void Close()
    {
        if (_connected)
        {
            try { _dll.Close(); }
            catch (Exception ex) { _log("warn", $"close warn: {ex.Message}"); }
            _connected = false;
        }
    }

    public void Dispose()
    {
        Close();
    }
}

public class VerifyMismatch : Exception
{
    public int Addr { get; }
    public int N { get; }

    public VerifyMismatch(int addr, int n) : base($"verify mismatch at 0x{addr:X8}: {n} bytes")
    {
        Addr = addr;
        N = n;
    }
}
