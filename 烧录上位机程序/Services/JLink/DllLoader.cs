using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// JLink_x64.dll 加载器与 P/Invoke 完整封装。
///
/// 关键：目标机器上 DLL 不在标准目录（不在 System32/PATH），
/// 必须在加载前通过 SetDllDirectory 指定搜索路径。
///
/// API 清单来源：src/core/probe/jlink_backend.py / pylink_rtt_backend.py
/// 逐项对应 pylink-square 1.6.0 对 JLinkARM DLL 的调用。
/// </summary>
public sealed class DllLoader : IDisposable
{
    #region kernel32

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    #endregion

    private IntPtr _dllHandle = IntPtr.Zero;
    private bool _disposed = false;

    #region JLinkARM structures

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct JLinkDeviceInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Manufacturer;
        public int FlashAddr;
        public int FlashSize;
        public int RAMAddr;
        public int RAMSize;
        public int Core;
        public int CoreId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JLinkRttBufDesc
    {
        public int ID;
        public int SizeOfBuffer;
        public int pBuffer;
        public int Next;
    }

    #endregion

    #region JLinkARM function delegates

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetDLLVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_EMU_GetListDelegate(IntPtr pList, int nMaxListSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_OpenDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_OpenWithSerialDelegate(int SerialNo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_OpenIPDelegate(string IpAddr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_CloseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetSerialNumberDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_SetTIFDelegate(int TIF);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_SetSpeedDelegate(int Speed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_ConnectDelegate(string Device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_ResetDelegate(int halt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_HaltDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GoDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_FlashDelegate(IntPtr pData, int Addr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_MemoryReadDelegate(int Addr, int Count, IntPtr pData, int nBits);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_MemoryWriteDelegate(int Addr, IntPtr pData, int Count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_MemoryWrite16Delegate(int Addr, IntPtr pData, int Count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetFirmwareVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetHardwareVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr JLINKARM_GetCoreNameDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetCoreIdDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetCoreCPUDellegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_EraseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_PowerOnDelegate(int Default);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_PowerOffDelegate(int Default);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_IsConnectedDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetNumDevicesDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_GetDeviceInfoDelegate(int Index, ref JLinkDeviceInfo Info, int InfoSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_RTT_StartDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_RTT_StopDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_RTT_ReadDelegate(int Channel, IntPtr pBuffer, int MaxSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_RTT_WriteDelegate(int Channel, IntPtr pData, int Size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_RTT_GetNumUpBuffersDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JLINKARM_RTT_GetBufDescDelegate(int Channel, int Up, ref JLinkRttBufDesc Desc);

    #endregion

    #region Cached delegates

    private int _dllVersion;

    #endregion

    public static DllLoader Current { get; private set; } = null!;

    public DllLoader()
    {
        Load();
        Current = this;
    }

    public static string? FindDllPath()
    {
        var candidates = new[]
        {
            null,
            @"C:\iar\ewarm-9.60.4\arm\bin\JLink_x64.dll",
            @"C:\Program Files\SEGGER\JLinkARM_DLL\JLink_x64.dll",
            @"C:\Program Files (x86)\SEGGER\JLinkARM_DLL\JLink_x64.dll",
        };

        foreach (var candidate in candidates)
        {
            if (candidate != null && File.Exists(candidate))
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

    private IntPtr GetProcAddress(string funcName)
    {
        var addr = GetProcAddress(_dllHandle, funcName);
        if (addr == IntPtr.Zero)
            throw new MissingMethodException($"JLink_x64.dll 中未找到函数: {funcName}");
        return addr;
    }

    #region JLinkARM API 封装

    public int GetDLLVersion()
    {
        var fn = GetDelegate<JLINKARM_GetDLLVersionDelegate>("JLINKARM_GetDLLVersion");
        return fn();
    }

    public int GetVersionMajor() => (GetDLLVersion() >> 16) & 0xFFFF;
    public int GetVersionMinor() => GetDLLVersion() & 0xFFFF;

    public int EnumEmulators(IntPtr pList, int nMaxListSize)
    {
        var fn = GetDelegate<JLINKARM_EMU_GetListDelegate>("JLINKARM_EMU_GetList");
        return fn(pList, nMaxListSize);
    }

    public int Open()
    {
        var fn = GetDelegate<JLINKARM_OpenDelegate>("JLINKARM_Open");
        return fn();
    }

    public int OpenWithSerial(int serialNo)
    {
        var fn = GetDelegate<JLINKARM_OpenWithSerialDelegate>("JLINKARM_OpenWithSerial");
        return fn(serialNo);
    }

    public int OpenIP(string ipAddr)
    {
        var fn = GetDelegate<JLINKARM_OpenIPDelegate>("JLINKARM_OpenIP");
        return fn(ipAddr);
    }

    public int Close()
    {
        var fn = GetDelegate<JLINKARM_CloseDelegate>("JLINKARM_Close");
        return fn();
    }

    public int GetSerialNumber()
    {
        var fn = GetDelegate<JLINKARM_GetSerialNumberDelegate>("JLINKARM_GetSerialNumber");
        return fn();
    }

    public int SetTIF(int tif)
    {
        var fn = GetDelegate<JLINKARM_SetTIFDelegate>("JLINKARM_SetTIF");
        return fn(tif);
    }

    public int SetSpeed(int speedKhz)
    {
        var fn = GetDelegate<JLINKARM_SetSpeedDelegate>("JLINKARM_SetSpeed");
        return fn(speedKhz);
    }

    public int Connect(string device)
    {
        var fn = GetDelegate<JLINKARM_ConnectDelegate>("JLINKARM_Connect");
        return fn(device);
    }

    public int Reset(int halt)
    {
        var fn = GetDelegate<JLINKARM_ResetDelegate>("JLINKARM_Reset");
        return fn(halt);
    }

    public int Halt()
    {
        var fn = GetDelegate<JLINKARM_HaltDelegate>("JLINKARM_Halt");
        return fn();
    }

    public int Go()
    {
        var fn = GetDelegate<JLINKARM_GoDelegate>("JLINKARM_Go");
        return fn();
    }

    public int Flash(IntPtr pData, int addr)
    {
        var fn = GetDelegate<JLINKARM_FlashDelegate>("JLINKARM_Flash");
        return fn(pData, addr);
    }

    public int MemoryRead(int addr, int count, byte[] buffer, int nBits = 32)
    {
        var fn = GetDelegate<JLINKARM_MemoryReadDelegate>("JLINKARM_MemoryRead");
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return fn(addr, count, handle.AddrOfPinnedObject(), nBits);
        }
        finally
        {
            handle.Free();
        }
    }

    public int MemoryWrite(int addr, IntPtr pData, int count)
    {
        var fn = GetDelegate<JLINKARM_MemoryWriteDelegate>("JLINKARM_MemoryWrite");
        return fn(addr, pData, count);
    }

    public int MemoryWrite16(int addr, IntPtr pData, int count)
    {
        var fn = GetDelegate<JLINKARM_MemoryWrite16Delegate>("JLINKARM_MemoryWrite16");
        return fn(addr, pData, count);
    }

    public int GetFirmwareVersion()
    {
        var fn = GetDelegate<JLINKARM_GetFirmwareVersionDelegate>("JLINKARM_GetFirmwareVersion");
        return fn();
    }

    public int GetHardwareVersion()
    {
        var fn = GetDelegate<JLINKARM_GetHardwareVersionDelegate>("JLINKARM_GetHardwareVersion");
        return fn();
    }

    public string GetCoreName()
    {
        var fn = GetDelegate<JLINKARM_GetCoreNameDelegate>("JLINKARM_GetCoreName");
        var ptr = fn();
        return Marshal.PtrToStringAnsi(ptr) ?? "";
    }

    public int GetCoreId()
    {
        var fn = GetDelegate<JLINKARM_GetCoreIdDelegate>("JLINKARM_GetCoreId");
        return fn();
    }

    public int GetCoreCPU()
    {
        var fn = GetDelegate<JLINKARM_GetCoreCPUDellegate>("JLINKARM_GetCoreCPU");
        return fn();
    }

    public int EraseChip()
    {
        var fn = GetDelegate<JLINKARM_EraseDelegate>("JLINKARM_Erase");
        return fn();
    }

    public int PowerOn(int def = 0)
    {
        var fn = GetDelegate<JLINKARM_PowerOnDelegate>("JLINKARM_PowerOn");
        return fn(def);
    }

    public int PowerOff(int def = 0)
    {
        var fn = GetDelegate<JLINKARM_PowerOffDelegate>("JLINKARM_PowerOff");
        return fn(def);
    }

    public int IsConnected()
    {
        var fn = GetDelegate<JLINKARM_IsConnectedDelegate>("JLINKARM_IsConnected");
        return fn();
    }

    public int GetNumDevices()
    {
        var fn = GetDelegate<JLINKARM_GetNumDevicesDelegate>("JLINKARM_GetNumDevices");
        return fn();
    }

    public JLinkDeviceInfo? GetDeviceInfo(int index)
    {
        var fn = GetDelegate<JLINKARM_GetDeviceInfoDelegate>("JLINKARM_GetDeviceInfo");
        var info = new JLinkDeviceInfo();
        int result = fn(index, ref info, Marshal.SizeOf(info));
        if (result == 0) return null;
        return info;
    }

    public int RTT_Start()
    {
        var fn = GetDelegate<JLINKARM_RTT_StartDelegate>("JLINKARM_RTT_Start");
        return fn();
    }

    public int RTT_Stop()
    {
        var fn = GetDelegate<JLINKARM_RTT_StopDelegate>("JLINKARM_RTT_Stop");
        return fn();
    }

    public int RTT_Read(int channel, IntPtr pBuffer, int maxSize)
    {
        var fn = GetDelegate<JLINKARM_RTT_ReadDelegate>("JLINKARM_RTT_Read");
        return fn(channel, pBuffer, maxSize);
    }

    public int RTT_Write(int channel, IntPtr pData, int size)
    {
        var fn = GetDelegate<JLINKARM_RTT_WriteDelegate>("JLINKARM_RTT_Write");
        return fn(channel, pData, size);
    }

    public int RTT_GetNumUpBuffers()
    {
        var fn = GetDelegate<JLINKARM_RTT_GetNumUpBuffersDelegate>("JLINKARM_RTT_GetNumUpBuffers");
        return fn();
    }

    public int RTT_GetBufDesc(int channel, int up, ref JLinkRttBufDesc desc)
    {
        var fn = GetDelegate<JLINKARM_RTT_GetBufDescDelegate>("JLINKARM_RTT_GetBufDesc");
        return fn(channel, up, ref desc);
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed && _dllHandle != IntPtr.Zero)
        {
            FreeLibrary(_dllHandle);
            _dllHandle = IntPtr.Zero;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
