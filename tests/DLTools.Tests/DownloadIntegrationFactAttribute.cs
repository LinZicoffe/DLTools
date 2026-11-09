using Xunit;

namespace DLTools.Tests;

/// <summary>本机 HttpListener 真实 HTTP 传输集成测试开关（与 IP 扫描实网开关同模式）。</summary>
public sealed class DownloadIntegrationFactAttribute : FactAttribute
{
    public DownloadIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLTOOLS_DOWNLOAD_INTEGRATION") != "1")
            Skip = "设置 DLTOOLS_DOWNLOAD_INTEGRATION=1 以执行本机 HttpListener 真实传输集成测试。";
    }
}
