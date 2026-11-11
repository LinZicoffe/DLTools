using System.IO;

namespace 烧录上位机程序.Services;

/// <summary>
/// 共享原子落盘收尾：把已完整写好的临时文件提交为目标文件。
/// 目标已存在用 File.Replace（同卷原子替换），否则 File.Move（全新创建）。
/// 该模式原先在 6 个 Store 各自复制，统一收口于此，避免各处语义漂移。
/// </summary>
public static class AtomicFileWriter
{
    public static void Commit(string temporaryPath, string targetPath)
    {
        if (File.Exists(targetPath)) File.Replace(temporaryPath, targetPath, null);
        else File.Move(temporaryPath, targetPath);
    }
}
