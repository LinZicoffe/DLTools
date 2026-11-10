using Xunit;

namespace DLTools.Tests;

/// <summary>真实环境集成门控：默认跳过，设置 DLTOOLS_DISKCLEAN_INTEGRATION=1 执行。
/// 扫描为只读；回收站用例在 %TEMP% 下创建一个小文件经真实 SHFileOperationW 送入回收站后验证原路径消失。</summary>
public sealed class DiskCleanupIntegrationFactAttribute : FactAttribute
{
    public DiskCleanupIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLTOOLS_DISKCLEAN_INTEGRATION") != "1")
            Skip = "设置 DLTOOLS_DISKCLEAN_INTEGRATION=1 以执行真实环境清理集成测试。";
    }
}

public class DiskCleanupIntegrationTests
{
    [DiskCleanupIntegrationFact]
    public void RealEnvironment_ScanIsReadOnlyAndProducesSummariesForAllRules()
    {
        var engine = new 烧录上位机程序.Services.Cleanup.CleanupEngine();
        var result = engine.Scan(烧录上位机程序.Models.Cleanup.CleanupSettings.Default, null, CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.Null(result.Error);
        Assert.Equal(烧录上位机程序.Services.Cleanup.CleanupRuleCatalog.Rules.Count, result.Summaries.Count);
        // 本机已知事实：ZCode 对话数据库目录存在且必然非零（1.4G 量级）——只展示规则必须如实上报。
        var db = result.Summaries.Single(s => s.RuleId == "zcode-session-db");
        Assert.True(db.TotalBytes > 0, $"zcode-session-db 应统计到 db.sqlite，实际 {db.TotalBytes}");
    }

    [DiskCleanupIntegrationFact]
    public void RealRecycleBin_SmallTempFileDisappearsFromSource()
    {
        var source = Path.Combine(Path.GetTempPath(), "dltools-cleaner-it-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(source, "recycle-bin integration fixture");
        try
        {
            var sink = new 烧录上位机程序.Services.Cleanup.ShellFileOperationRecycleBin();
            Assert.True(sink.TrySend(source, out var error), $"SHFileOperationW 失败：{error}");
            Assert.False(File.Exists(source)); // 已移入回收站；回收站中的副本可手动还原
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source); // 失败路径下的兜底清理
        }
    }
}
