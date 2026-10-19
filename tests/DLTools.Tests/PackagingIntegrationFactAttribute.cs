using Xunit;

namespace DLTools.Tests;

public sealed class PackagingIntegrationFactAttribute : FactAttribute
{
    public PackagingIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLTOOLS_INTEGRATION") != "1")
            Skip = "设置 DLTOOLS_INTEGRATION=1 以执行本机真实构建/Inno/安装集成测试。";
    }
}
