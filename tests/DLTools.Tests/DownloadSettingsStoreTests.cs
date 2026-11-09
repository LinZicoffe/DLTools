using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadSettingsStoreTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private DownloadSettingsStore CreateStore()
        => new(Path.Combine(_workspace.Root, "settings.json"));

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = CreateStore().Load();
        Assert.Equal(new DownloadSettings(), settings);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsValues()
    {
        var store = CreateStore();
        store.Save(new DownloadSettings
        {
            DefaultConnections = 6,
            MaxConcurrentTasks = 2,
            MaxRateKB = 2048,
            ProxyHost = "proxy.internal.example",
            ProxyPort = 8080
        });
        var loaded = store.Load();
        Assert.Equal(6, loaded.DefaultConnections);
        Assert.Equal(2, loaded.MaxConcurrentTasks);
        Assert.Equal(2048, loaded.MaxRateKB);
        Assert.Equal("proxy.internal.example", loaded.ProxyHost);
        Assert.Equal(8080, loaded.ProxyPort);
        Assert.True(loaded.HasProxy);
        Assert.Empty(Directory.GetFiles(_workspace.Root, "*.tmp"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(99, 16)]
    public void Save_OutOfRangeConnections_IsClampedOnRoundTrip(int saved, int expected)
    {
        var store = CreateStore();
        store.Save(new DownloadSettings { DefaultConnections = saved });
        Assert.Equal(expected, store.Load().DefaultConnections);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 8)]
    public void Save_OutOfRangeConcurrentTasks_IsClampedOnRoundTrip(int saved, int expected)
    {
        var store = CreateStore();
        store.Save(new DownloadSettings { MaxConcurrentTasks = saved });
        Assert.Equal(expected, store.Load().MaxConcurrentTasks);
    }

    [Fact]
    public void Load_CorruptFile_ThrowsAndKeepsFileForRepair()
    {
        var path = Path.Combine(_workspace.Root, "settings.json");
        File.WriteAllText(path, "{ broken");
        var error = Assert.Throws<IOException>(() => new DownloadSettingsStore(path).Load());
        Assert.Contains("无法读取", error.Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_FutureSchemaVersion_ThrowsWithVersionHint()
    {
        var path = Path.Combine(_workspace.Root, "settings.json");
        File.WriteAllText(path, """{"SchemaVersion":99}""");
        var error = Assert.Throws<IOException>(() => new DownloadSettingsStore(path).Load());
        Assert.Contains("99", error.Message);
    }

    public void Dispose() => _workspace.Dispose();
}
