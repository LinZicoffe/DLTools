using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>NTFS 稀疏文件助手：大文件预分配先标稀疏再 SetLength，避免同步零填充整段磁盘；
/// 提交前清稀疏位（文件已写满无空洞，清位免稀疏碎片拖慢顺序读）。
/// 任何失败（FAT/exFAT/网络盘/权限/非 Windows）一律返回 false 绝不抛，调用方无条件继续常规预分配。</summary>
public static class SparseFile
{
    /// <summary>引擎策略阈值：小于此长度不值得稀疏 IOCTL 开销（助手本身不限长度）。</summary>
    public const long MinSparseLength = 5 * 1024 * 1024;

    private const uint FsctlSetSparse = 0x000900C4;
    private const uint FileAttributeSparseFile = 0x200;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;

    /// <summary>必须在 SetLength 之前调用：NTFS 对非稀疏文件的扩展会同步写零。</summary>
    public static bool TryMarkSparse(SafeFileHandle handle) => SetSparse(handle, true);

    /// <summary>尽力而为清位；非稀疏文件上调用同样安全。</summary>
    public static bool TryClearSparse(SafeFileHandle handle) => SetSparse(handle, false);

    public static bool IsSparse(string path)
    {
        try
        {
            var attributes = GetFileAttributesW(path);
            return attributes != InvalidFileAttributes && (attributes & FileAttributeSparseFile) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SetSparse(SafeFileHandle handle, bool sparse)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed) return false;
        try
        {
            // FSCTL_SET_SPARSE 输入为 BOOLEAN（1 字节）；同步 IOCTL，快速元数据操作，异步句柄上可接受
            var input = sparse ? (byte)1 : (byte)0;
            return DeviceIoControl(handle, FsctlSetSparse, ref input, sizeof(byte),
                IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        ref byte lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);
}