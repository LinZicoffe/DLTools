using System.Text.Json;
using Xunit;
using 烧录上位机程序.Models.Cleanup;
using 烧录上位机程序.Services.Cleanup;

namespace DLTools.Tests;

public class DiskCleanupSettingsStoreTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private string SettingsPath => Path.Combine(_workspace.Root, "settings.json");

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new CleanupSettingsStore(SettingsPath);
        Assert.Equal(CleanupSettings.Default, store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var store = new CleanupSettingsStore(SettingsPath);
        store.Save(new CleanupSettings
        {
            FreshnessHours = 48,
            LogAgeDays = 14,
            ArtifactAgeDays = 60,
            ConversationAgeDays = 120,
            BackupKeepCount = 5
        });
        var loaded = store.Load();
        Assert.Equal(48, loaded.FreshnessHours);
        Assert.Equal(14, loaded.LogAgeDays);
        Assert.Equal(60, loaded.ArtifactAgeDays);
        Assert.Equal(120, loaded.ConversationAgeDays);
        Assert.Equal(5, loaded.BackupKeepCount);
    }

    [Fact]
    public void Load_ClampsOutOfRangeValuesIntoLegalRange()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new CleanupSettingsStore.SettingsDocument
        {
            FreshnessHours = -5,
            LogAgeDays = 0,
            ArtifactAgeDays = 99999,
            ConversationAgeDays = 7,
            BackupKeepCount = 0
        }));
        var loaded = new CleanupSettingsStore(SettingsPath).Load();
        Assert.Equal(1, loaded.FreshnessHours);
        Assert.Equal(1, loaded.LogAgeDays);
        Assert.Equal(3650, loaded.ArtifactAgeDays);
        Assert.Equal(7, loaded.ConversationAgeDays);
        Assert.Equal(1, loaded.BackupKeepCount);
    }

    [Fact]
    public void Load_KeepsLegalBoundaryValuesUntouched()
    {
        // 钳制是闭区间：恰好落在边界上的合法用户设置必须原值保留（开区间/越界−1 的实现会被此用例拦截）。
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new CleanupSettingsStore.SettingsDocument
        {
            FreshnessHours = 720,
            LogAgeDays = 3650,
            ArtifactAgeDays = 3650,
            ConversationAgeDays = 3650,
            BackupKeepCount = 20
        }));
        var upper = new CleanupSettingsStore(SettingsPath).Load();
        Assert.Equal(720, upper.FreshnessHours);
        Assert.Equal(3650, upper.LogAgeDays);
        Assert.Equal(3650, upper.ArtifactAgeDays);
        Assert.Equal(3650, upper.ConversationAgeDays);
        Assert.Equal(20, upper.BackupKeepCount);

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new CleanupSettingsStore.SettingsDocument
        {
            FreshnessHours = 1,
            LogAgeDays = 1,
            ArtifactAgeDays = 1,
            ConversationAgeDays = 1,
            BackupKeepCount = 1
        }));
        var lower = new CleanupSettingsStore(SettingsPath).Load();
        Assert.Equal(1, lower.FreshnessHours);
        Assert.Equal(1, lower.LogAgeDays);
        Assert.Equal(1, lower.BackupKeepCount);
    }

    [Fact]
    public void Load_CorruptFile_ThrowsWithoutSilentReset()
    {
        File.WriteAllText(SettingsPath, "{ not-json");
        var store = new CleanupSettingsStore(SettingsPath);
        var error = Assert.Throws<IOException>(() => store.Load());
        Assert.Contains("清理设置无法读取", error.Message);
        Assert.Equal("{ not-json", File.ReadAllText(SettingsPath)); // 原文件保留供修复
    }

    public void Dispose() => _workspace.Dispose();
}
