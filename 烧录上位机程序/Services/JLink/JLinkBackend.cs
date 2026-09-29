using System;
using System.Linq;
using System.Runtime.InteropServices;
using 烧录上位机程序.Models;
using 烧录上位机程序.Services;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// J-Link 烧录后端：直接调用 JLink_x64.dll 的 P/Invoke 封装。
///
/// 连接序列严格按 Python 源码 jlink_backend.py 的 flash 版协议：
/// open → close → open(serial) → set_tif → set_speed → connect。
/// set_tif 是 SWD/JTAG 二选一（不可 OR 起来）。
/// </summary>
public sealed class JLinkBackend : IDisposable
{
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

    public const int JLINK_IF_SWD = 1;
    public const int JLINK_IF_JTAG = 2;
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
            _log("info", $"J-Link 远程模式: {remote}");
            if (_dll.IsConnected() == 0)
            {
                _dll.OpenIP(remote);
                ConnectedSerial = _dll.GetSerialNumber().ToString();
                _dll.Close();
                _dll.OpenIP(remote);
            }
        }
        else
        {
            if (_dll.IsConnected() == 0)
            {
                if (string.IsNullOrEmpty(serial) || serial == "0")
                {
                    _dll.Open();
                    var sn = _dll.GetSerialNumber();
                    ConnectedSerial = sn.ToString();
                    _dll.Close();
                    _dll.OpenWithSerial(int.Parse(ConnectedSerial));
                }
                else
                {
                    _dll.OpenWithSerial(int.Parse(serial));
                    var sn = _dll.GetSerialNumber();
                    ConnectedSerial = sn.ToString();
                    _dll.Close();
                    _dll.OpenWithSerial(int.Parse(ConnectedSerial));
                }
            }
            _log("info", $"J-Link SN: {ConnectedSerial}");
        }

        int tif = p.Interface == "SWD" ? JLINK_IF_SWD : JLINK_IF_JTAG;
        _dll.SetTIF(tif);
        _dll.SetSpeed(p.SpeedKhz);
        _dll.Connect(p.DeviceName);
        _connected = true;
        _log("info", $"目标已连接: {p.DeviceName}");
    }

    public void Erase(string mode)
    {
        if (mode == EraseModeChip)
        {
            _log("info", "开始整片擦除...");
            int result = _dll.EraseChip();
            if (result != 0)
                throw new InvalidOperationException($"擦除失败，错误码: {result}");
            _log("info", "整片擦除完成");
        }
    }

    public void Program(string filePath, string fileFormat, int binStartAddr, Action<int, int> onProgress)
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
            int len = endExclusive - start;
            var data = ih.ToBinArray(start, endExclusive);
            if (data.Length == 0) continue;

            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _dll.Flash(handle.AddrOfPinnedObject(), start);
            }
            finally
            {
                handle.Free();
            }

            done += len;
            int percent = Math.Min(100, done * 100 / totalBytes);
            onProgress?.Invoke(percent, 100);
            _log("info", $"已烧录 {len} B @ 0x{start:X8}");
        }
    }

    public void Verify(string filePath, string fileFormat, int binStartAddr)
    {
        if (!_connected)
            throw new InvalidOperationException("未连接");

        var ih = FlashFileParser.ToIntelHex(filePath, binStartAddr);
        foreach (var (start, endExclusive) in ih.Segments)
        {
            int len = endExclusive - start;
            var expected = ih.ToBinArray(start, endExclusive);
            if (expected.Length == 0) continue;

            var buffer = new byte[len];
            _dll.MemoryRead(start, len, buffer, 32);

            for (int i = 0; i < len; i++)
            {
                if (buffer[i] != expected[i])
                    throw new VerifyMismatch(start + i, len);
            }
        }
    }

    public void Reset(bool halt, bool run)
    {
        if (halt && !run)
        {
            _dll.Reset(1);
            _dll.Halt();
            _log("info", "CPU halted after reset");
        }
        else if (run)
        {
            _dll.Reset(0);
            _dll.Go();
            _log("info", "CPU running");
        }
        else
        {
            _dll.Reset(0);
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
