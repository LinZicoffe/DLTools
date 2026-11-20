using Xunit;

namespace DLTools.Tests;

/// <summary>需要真实 NTFS 卷的用例（稀疏文件 IOCTL 语义）；临时目录所在卷非 NTFS 时显式跳过。</summary>
public sealed class NtfsFactAttribute : FactAttribute
{
    public NtfsFactAttribute()
    {
        if (!TempDriveIsNtfs())
            Skip = "临时目录所在卷不是 NTFS，稀疏文件用例跳过。";
    }

    internal static bool TempDriveIsNtfs()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            return root is not null
                && new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>稀疏 + 真实 HTTP 双重门控：DLTOOLS_DOWNLOAD_INTEGRATION=1 且临时目录卷为 NTFS。</summary>
public sealed class NtfsDownloadIntegrationFactAttribute : FactAttribute
{
    public NtfsDownloadIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLTOOLS_DOWNLOAD_INTEGRATION") != "1")
            Skip = "设置 DLTOOLS_DOWNLOAD_INTEGRATION=1 以执行本机 HttpListener 真实传输集成测试。";
        else if (!NtfsFactAttribute.TempDriveIsNtfs())
            Skip = "临时目录所在卷不是 NTFS，稀疏文件用例跳过。";
    }
}