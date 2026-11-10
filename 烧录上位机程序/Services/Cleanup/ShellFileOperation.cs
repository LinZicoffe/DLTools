using System.Runtime.InteropServices;

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
        // pFrom 要求双 NUL 结尾；超过 MAX_PATH 的路径用 \\?\ 扩展前缀（SHFileOperationW 支持）。
        var from = path.Length >= 260 && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\\?\" + path
            : path;
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
                : $"SHFileOperationW 失败（代码 {result}）";
            return false;
        }
        error = null;
        return true;
    }
}
