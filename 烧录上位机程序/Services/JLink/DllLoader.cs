using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// J-Link DLL 调用异常（对应 pylink 的 JLinkException 家族）。
/// </summary>
public class JLinkDllException : Exception
{
    public JLinkDllException(string message) : base(message) { }
}

/// <summary>
/// JLink_x64.dll 加载器与 P/Invoke 封装。
///
/// 关键：目标机器上 DLL 不在标准目录（不在 System32/PATH），
/// 必须在加载前通过 SetDllDirectory 指定搜索路径。
///
/// API 映射依据（与 pylink-square 1.6.0 对 DLL 的调用逐项对齐，
/// 导出名全部经 V9.76 真实导出表核验，清单见 docs/JLink_DLL_exports.txt）：
/// - 打开：JLINKARM_EMU_SelectByUSBSN / JLINKARM_SelectIP / JLINKARM_SelectUSB
///         + JLINKARM_OpenEx（返回错误字符串指针，NULL 表示成功）
/// - 设备：JLINKARM_DEVICE_GetIndex 校验 + JLINKARM_ExecCommand("Device = &lt;名&gt;")
/// - 烧录：JLINKARM_BeginDownload(0) → JLINKARM_WriteMem → JLINKARM_EndDownload
/// - 擦除：JLINK_EraseChip（注意前缀是 JLINK_，无 ARM）
/// - 校验：JLINKARM_ReadMemEx（按字节访问，访问宽度 flags=0）
///
/// 调用约定：pylink library.py 的 _standard_calls_ 指明 Windows 下
/// JLINKARM_WriteMem / BeginDownload / EndDownload / JLINK_EraseChip 为
/// __stdcall，其余为 __cdecl（x64 单一调用约定，标注仅保持语义正确）。
/// </summary>
public sealed class DllLoader : IDisposable
{
    #region kernel32

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    #endregion

    private IntPtr _dllHandle = IntPtr.Zero;
    private bool _disposed = false;
    private bool _open = false;

    #region 导出函数委托（ cdecl 除非注明 stdcall ）

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDLLVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IsOpenDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EmuIsConnectedDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IsConnectedDelegate();

    // OpenEx 两个日志回调传 NULL（SEGGER 允许），失败信息经返回的 char* 传递
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenExDelegate(IntPtr logFunc, IntPtr errorFunc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EmuSelectByUsbSnDelegate(int serialNo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SelectIpDelegate([MarshalAs(UnmanagedType.LPStr)] string ip, int port);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SelectUsbDelegate(int port);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSNDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TifSelectDelegate(int tif);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSpeedDelegate(int speedKhz);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceGetIndexDelegate([MarshalAs(UnmanagedType.LPStr)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExecCommandDelegate([MarshalAs(UnmanagedType.LPStr)] string command, byte[] errBuf, int bufSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ConnectDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetResetDelayDelegate(int ms);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ResetDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HaltDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IsHaltedDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GoExDelegate(int numInstructions, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadMemExDelegate(int addr, int numBytes, IntPtr pData, int accessFlags);

    // ↓ pylink _standard_calls_：Windows 下为 __stdcall
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WriteMemDelegate(int addr, int numBytes, IntPtr pData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int BeginDownloadDelegate(int flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndDownloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EraseChipDelegate();

    #endregion

    public static DllLoader Current { get; private set; } = null!;

    public DllLoader()
    {
        Load();
        Current = this;
    }

    public static string? FindDllPath()
    {
        // exe 同目录优先：支持把 DLL 放在程序旁边的绿色部署
        // （且不先探测 exe 目录的话，FindDllPath 返回 null 会直接抛异常，
        //   根本走不到 LoadLibrary 本可命中的默认搜索）
        // 官方安装目录次之：IAR 等工具链捆绑的 JLink DLL 版本常较旧，
        // 若排在前面会被静默优先选用
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "JLink_x64.dll"),
            @"C:\Program Files\SEGGER\JLinkARM_DLL\JLink_x64.dll",
            @"C:\Program Files (x86)\SEGGER\JLinkARM_DLL\JLink_x64.dll",
            @"C:\iar\ewarm-9.60.4\arm\bin\JLink_x64.dll",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';'))
            {
                var fullPath = Path.Combine(dir.Trim(), "JLink_x64.dll");
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }
        catch { }

        return null;
    }

    private void Load()
    {
        var dllPath = FindDllPath();
        if (dllPath == null)
            throw new DllNotFoundException("未找到 JLink_x64.dll。请确认已安装 SEGGER J-Link 驱动。");

        var dir = Path.GetDirectoryName(dllPath)!;
        SetDllDirectory(dir);

        _dllHandle = LoadLibrary(dllPath);
        if (_dllHandle == IntPtr.Zero)
            throw new DllNotFoundException($"加载 JLink_x64.dll 失败 (路径: {dllPath}, 错误码: {Marshal.GetLastWin32Error()})");
    }

    private T GetDelegate<T>(string funcName) where T : class
    {
        if (_dllHandle == IntPtr.Zero)
            throw new InvalidOperationException("DLL 未加载");
        var addr = GetProcAddress(_dllHandle, funcName);
        if (addr == IntPtr.Zero)
            throw new MissingMethodException($"JLink_x64.dll 中未找到函数: {funcName}");
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    #region JLinkARM API 封装

    public int GetDLLVersion()
    {
        return GetDelegate<GetDLLVersionDelegate>("JLINKARM_GetDLLVersion")();
    }

    /// <summary>DLL 是否已打开仿真器（JLINKARM_IsOpen）。</summary>
    public bool IsDllOpen()
    {
        return GetDelegate<IsOpenDelegate>("JLINKARM_IsOpen")() != 0;
    }

    /// <summary>仿真器连接状态（JLINKARM_EMU_IsConnected）。</summary>
    public bool EmuIsConnected()
    {
        return GetDelegate<EmuIsConnectedDelegate>("JLINKARM_EMU_IsConnected")() != 0;
    }

    /// <summary>目标芯片连接状态（pylink target_connected：已打开 + 仿真器连 + CPU 连）。</summary>
    public bool IsTargetConnected()
    {
        return IsDllOpen()
            && EmuIsConnected()
            && GetDelegate<IsConnectedDelegate>("JLINKARM_IsConnected")() != 0;
    }

    /// <summary>按 USB 序列号打开（pylink open(serial_no=...)）。</summary>
    public void OpenBySerial(int serialNo)
    {
        EnsureClosed(); // pylink open() 首行先 close，避免句柄计数错乱
        int res = GetDelegate<EmuSelectByUsbSnDelegate>("JLINKARM_EMU_SelectByUSBSN")(serialNo);
        if (res < 0)
            throw new JLinkDllException($"未找到序列号 {serialNo} 的 J-Link，请检查 USB 连接");
        OpenExCore();
    }

    /// <summary>按 IP 打开（pylink open(ip_addr="ip:port") 走 JLINKARM_SelectIP(字符串, 端口)）。</summary>
    public void OpenByIp(string ip, int port)
    {
        EnsureClosed();
        int res = GetDelegate<SelectIpDelegate>("JLINKARM_SelectIP")(ip, port);
        if (res != 0)
            throw new JLinkDllException($"无法连接 {ip}:{port} 处的 J-Link 仿真器");
        OpenExCore();
    }

    /// <summary>打开默认 USB 仿真器（pylink open() 无参路径：SelectUSB(0)）。</summary>
    public void OpenDefault()
    {
        EnsureClosed();
        int res = GetDelegate<SelectUsbDelegate>("JLINKARM_SelectUSB")(0);
        if (res != 0)
            throw new JLinkDllException("无法连接默认 J-Link 仿真器");
        OpenExCore();
    }

    private void OpenExCore()
    {
        IntPtr err = GetDelegate<OpenExDelegate>("JLINKARM_OpenEx")(IntPtr.Zero, IntPtr.Zero);
        if (err != IntPtr.Zero)
        {
            string msg = Marshal.PtrToStringAnsi(err) ?? "未知错误";
            throw new JLinkDllException($"打开 J-Link 失败: {msg}");
        }
        _open = true;
    }

    /// <summary>关闭（JLINKARM_Close，幂等）。</summary>
    public void Close()
    {
        if (!_open) return;
        GetDelegate<CloseDelegate>("JLINKARM_Close")();
        _open = false;
    }

    /// <summary>当前打开仿真器的序列号（JLINKARM_GetSN）。</summary>
    public int GetSN()
    {
        return GetDelegate<GetSNDelegate>("JLINKARM_GetSN")();
    }

    /// <summary>
    /// 选择目标接口（JLINKARM_TIF_Select）。
    /// TIF 值与 pylink enums.JLinkInterfaces 一致：JTAG=0, SWD=1。
    /// 返回 0 成功 / 1 失败。
    /// </summary>
    public void SetTif(int tif)
    {
        int res = GetDelegate<TifSelectDelegate>("JLINKARM_TIF_Select")(tif);
        if (res != 0)
            throw new JLinkDllException($"选择调试接口失败 (TIF={tif})，当前 J-Link 可能不支持该接口");
    }

    public void SetSpeed(int speedKhz)
    {
        GetDelegate<SetSpeedDelegate>("JLINKARM_SetSpeed")(speedKhz);
    }

    /// <summary>
    /// 连接目标芯片（pylink connect() 三步）：
    /// 1. JLINKARM_DEVICE_GetIndex 校验设备名受支持（&lt;=0 不支持）
    /// 2. ExecCommand("Device = &lt;名&gt;"）—— DLL 无 SetDevice 导出，设备选择只能走命令通道
    /// 3. JLINKARM_Connect()（无参数，&lt;0 失败）
    /// </summary>
    public void Connect(string deviceName)
    {
        int index = GetDelegate<DeviceGetIndexDelegate>("JLINKARM_DEVICE_GetIndex")(deviceName);
        if (index <= 0)
            throw new JLinkDllException($"不支持的设备: {deviceName}，请检查设备名（参考 SEGGER 支持设备列表）");

        ExecCommand($"Device = {deviceName}");

        int res = GetDelegate<ConnectDelegate>("JLINKARM_Connect")();
        if (res < 0)
            throw new JLinkDllException($"连接目标失败，错误码: {res}");
    }

    /// <summary>执行 DLL 命令（JLINKARM_ExecCommand，错误写入 256 字节缓冲）。</summary>
    public void ExecCommand(string command)
    {
        const int ErrBufSize = 256; // SEGGER 文档的错误缓冲口径（pylink 实现用 336，取 256 均安全）
        var errBuf = new byte[ErrBufSize];
        GetDelegate<ExecCommandDelegate>("JLINKARM_ExecCommand")(command, errBuf, ErrBufSize);
        int end = Array.IndexOf(errBuf, (byte)0);
        if (end < 0) end = errBuf.Length;
        string err = Encoding.ASCII.GetString(errBuf, 0, end).Trim();
        if (err.Length > 0)
            throw new JLinkDllException($"ExecCommand 失败: {command} → {err}");
    }

    public void SetResetDelay(int ms)
    {
        GetDelegate<SetResetDelayDelegate>("JLINKARM_SetResetDelay")(ms);
    }

    /// <summary>复位目标（JLINKARM_Reset，无参数；&lt;0 失败）。</summary>
    public void Reset()
    {
        int res = GetDelegate<ResetDelegate>("JLINKARM_Reset")();
        if (res < 0)
            throw new JLinkDllException($"复位失败，错误码: {res}");
    }

    /// <summary>暂停 CPU（JLINKARM_Halt，返回 0 表示成功）。</summary>
    public void Halt()
    {
        int res = GetDelegate<HaltDelegate>("JLINKARM_Halt")();
        if (res != 0)
            throw new JLinkDllException($"暂停 CPU 失败，错误码: {res}");
    }

    /// <summary>恢复运行（pylink restart()：目标处于 halted 时调 GoEx(0,0)；负返回为错误码不算 halted）。</summary>
    public void Restart()
    {
        if (GetDelegate<IsHaltedDelegate>("JLINKARM_IsHalted")() > 0)
            GetDelegate<GoExDelegate>("JLINKARM_GoEx")(0, 0);
    }

    /// <summary>CPU 是否处于暂停态（JLINKARM_IsHalted &gt; 0）。用于复位后如实上报运行状态。</summary>
    public bool IsCpuHalted()
    {
        return GetDelegate<IsHaltedDelegate>("JLINKARM_IsHalted")() > 0;
    }

    /// <summary>整片擦除（JLINK_EraseChip；&lt;0 失败，正值为擦除字节数）。</summary>
    public void EraseChip()
    {
        int res = GetDelegate<EraseChipDelegate>("JLINK_EraseChip")();
        if (res < 0)
            throw new JLinkDllException($"整片擦除失败，错误码: {res}");
    }

    /// <summary>开始下载事务（pylink flash()：Begin → WriteMem → End）。</summary>
    public void BeginDownload(int flags = 0)
    {
        int res = GetDelegate<BeginDownloadDelegate>("JLINKARM_BeginDownload")(flags);
        if (res < 0)
            throw new JLinkDllException($"BeginDownload 失败，错误码: {res}");
    }

    /// <summary>结束下载事务并触发实际编程（&lt;0 失败）。</summary>
    public void EndDownload()
    {
        int res = GetDelegate<EndDownloadDelegate>("JLINKARM_EndDownload")();
        if (res < 0)
            throw new JLinkDllException($"EndDownload 失败（编程/校验未完成），错误码: {res}");
    }

    /// <summary>
    /// 写入内存（JLINKARM_WriteMem，stdcall）。DLL 在下载事务内自动按扇区擦除+编程。
    /// 返回写入字节数。
    /// </summary>
    public int WriteMem(int addr, byte[] data)
    {
        var fn = GetDelegate<WriteMemDelegate>("JLINKARM_WriteMem");
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            return fn(addr, data.Length, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 读取内存（JLINKARM_ReadMemEx，按字节访问 accessFlags=0，与 pylink memory_read 默认一致）。
    /// 返回读取字节数，&lt;0 抛异常。
    /// </summary>
    public int ReadMem(int addr, byte[] buffer)
    {
        var fn = GetDelegate<ReadMemExDelegate>("JLINKARM_ReadMemEx");
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            int res = fn(addr, buffer.Length, handle.AddrOfPinnedObject(), 0);
            if (res < 0)
                throw new JLinkDllException($"读取内存失败 @ 0x{addr:X8}，错误码: {res}");
            return res;
        }
        finally
        {
            handle.Free();
        }
    }

    private void EnsureClosed()
    {
        if (_open)
            Close();
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed && _dllHandle != IntPtr.Zero)
        {
            try { Close(); } catch { }
            FreeLibrary(_dllHandle);
            _dllHandle = IntPtr.Zero;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
