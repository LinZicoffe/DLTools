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
