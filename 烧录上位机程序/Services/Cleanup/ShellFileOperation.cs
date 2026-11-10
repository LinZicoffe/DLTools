using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>回收站删除出口。接口便于测试注入假实现；真实实现经 SHFileOperationW（FOF_ALLOWUNDO），
/// 不引入 Microsoft.VisualBasic / WindowsForms 依赖。</summary>
public interface IRecycleBinSink
{
    /// <summary>把单个文件或目录送入回收站。返回 false 表示失败（错误原因经 error 给出）。</summary>
    bool TrySend(string path, out string? error);
}

public sealed class ShellFileOperationRecycleBin : IRecycleBinSink
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040; // 删除可撤销 → 送入回收站
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_NO_CONNECTED_ELEMENTS = 0x2000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFileOpStructW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperationW(ref SHFileOpStructW fileOperation);

    public bool TrySend(string path, out string? error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空";
            return false;
        }
        // pFrom 要求双 NUL 结尾。先按原样路径尝试（shell 命名空间对普通路径最可靠）；
        // 仅当原样失败且路径超过 MAX_PATH 时，再以 \\?\ 扩展前缀重试一次——个别系统版本
        // 对前缀支持不一致，因此前缀是重试路径而不是首选路径，两次都失败才计为失败（fail-closed）。
        if (TryOperate(path, out error)) return true;
        if (path.Length >= 260 && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && TryOperate(@"\\?\" + path, out error))
            return true;
        return false;
    }

    private static bool TryOperate(string from, out string? error)
    {
        var operation = new SHFileOpStructW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = from + "\0",
            pTo = null,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI |
                FOF_NO_CONNECTED_ELEMENTS)
        };
        var result = SHFileOperationW(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
        {
            error = operation.fAnyOperationsAborted
                ? "操作被中止"
                : $"SHFileOperationW 失败（代码 {result}，路径长度 {from.Length}）";
            return false;
        }
        error = null;
        return true;
    }
}

/// <summary>句柄式永久删除（封堵「二次校验通过之后、路径删除之前」父目录被换成 junction 的微窗口）。
/// 本机实测（Win11 26200）SetFileInformationByHandle(FileDispositionInfo) 对所有变体返回拒绝访问，
/// 而 FILE_FLAG_DELETE_ON_CLOSE 可用——因此采用两段式：
/// 第一段以 OPEN_REPARSE_POINT 打开并读取句柄属性（不布防删除，重解析点/云占位在此拒绝且零副作用）；
/// 第二段立即以 DELETE_ON_CLOSE|OPEN_REPARSE_POINT 重新打开，句柄关闭即删除。
/// 路径解析只发生在两次打开瞬间；此后任何替换都影响不到被钉住的对象。OPEN_REPARSE_POINT 保证
/// 即便第二段打开瞬间路径已被换成链接，打开与删除的也是链接本体而非其目标——绝不会穿透链接删目标数据
/// （代价是这一极端竞态下植入的链接本体被移除，审计按拒绝记录）。
/// 行为与旧 File.Delete 对齐：文件已不存在视为成功；占用/只读失败给出原因。</summary>
internal static class SafeFileDelete
{
    private const uint DesiredAccessDelete = 0x0001; // DELETE
    private const uint DesiredAccess = DesiredAccessDelete | 0x00100000; // + SYNCHRONIZE
    private const uint FileShareReadWriteDelete = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagDeleteOnClose = 0x04000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const uint FileAttributeRecallOnDataAccess = 0x00400000;
    private const uint FileAttributeRecallOnOpen = 0x00040000;

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // 与原生 BY_HANDLE_FILE_INFORMATION 的 DWORD 对齐一致
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    /// <summary>删除单个文件。返回 false 时 reason 给出失败原因；返回 true 表示对象已被删除（或本就不存在），
    /// 此时 reason 为 null 或「已删除但带告警」的说明（布防句柄已打开后属性复核失败/复核拒绝的极端竞态：
    /// 删除无法撤销，如实按已删除上报并把告警写入 reason，保证审计与事实一致）。</summary>
    public static bool TryDelete(string path, out string? reason)
    {
        // 第一段：探针句柄——只读属性不布防，拒绝路径无任何副作用。
        using (var probe = CreateFileW(path, DesiredAccess, FileShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero))
        {
            if (TryClassifyOpenFailure(probe, out reason, out var result)) return result;
            if (!GetFileInformationByHandle(probe, out var probeInfo))
            {
                reason = $"读取属性失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                return false;
            }
            if (!VerifyPinnedObject(probeInfo, out reason)) return false;
        }

        // 第二段：布防句柄——打开成功即删除不可避免（关闭即删，布防不可撤销）。
        using (var deleter = CreateFileW(path, DesiredAccess, FileShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, FileFlagDeleteOnClose | FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero))
        {
            if (TryClassifyOpenFailure(deleter, out reason, out var result)) return result;
            if (!GetFileInformationByHandle(deleter, out var deleteInfo))
            {
                // 极端竞态：已布防但属性读不出。删除将发生——按已删除上报并附告警，审计不与事实相悖。
                reason = "警告：布防后属性复核失败，对象已随句柄关闭进入删除流程";
                return true;
            }
            if (!VerifyPinnedObject(deleteInfo, out var verifyReason))
            {
                // 极端竞态：布防瞬间对象被换（如换成链接）。被删除的是被钉住的对象本体而非其目标
                //（OPEN_REPARSE_POINT 不穿透；非空目录变体在关闭时不会被删，措辞用「进入删除流程」如实表述）。
                reason = $"警告：布防后复核发现对象已变为不可删除类（{verifyReason}），该对象本体已随句柄关闭进入删除流程";
                return true;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>打开失败统一分类。返回 true 表示已定性（result 即 TryDelete 结果）：
    /// 已消失=成功（与 File.Delete 一致）；共享冲突=占用失败；其余如实报告 Win32 错误码。句柄有效则未定性。</summary>
    private static bool TryClassifyOpenFailure(SafeFileHandle handle, out string? reason, out bool result)
    {
        if (!handle.IsInvalid)
        {
            reason = null;
            result = false;
            return false;
        }
        var code = Marshal.GetLastWin32Error();
        if (code is ErrorFileNotFound or ErrorPathNotFound)
        {
            reason = null;
            result = true;
            return true;
        }
        reason = code is ErrorSharingViolation or ErrorLockViolation
            ? "文件被占用"
            : $"打开失败（Win32 错误 {code}）";
        result = false;
        return true;
    }

    /// <summary>在句柄（已钉住的对象）上复核安全属性——打开瞬间的路径形态不再重要。</summary>
    private static bool VerifyPinnedObject(ByHandleFileInformation information, out string? reason)
    {
        if ((information.FileAttributes & (uint)System.IO.FileAttributes.ReparsePoint) != 0)
        {
            reason = "链接或重解析点";
            return false;
        }
        if ((information.FileAttributes & (uint)System.IO.FileAttributes.Offline) != 0 ||
            (information.FileAttributes & FileAttributeRecallOnDataAccess) != 0 ||
            (information.FileAttributes & FileAttributeRecallOnOpen) != 0)
        {
            reason = "云占位文件";
            return false;
        }
        if ((information.FileAttributes & (uint)System.IO.FileAttributes.Directory) != 0)
        {
            reason = "目录不经文件删除通道";
            return false;
        }
        reason = null;
        return true;
    }
}
