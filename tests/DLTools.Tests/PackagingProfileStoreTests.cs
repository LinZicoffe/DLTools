using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public class PackagingProfileStoreTests
{
    [Fact]
    public void StableAppId_SurvivesReloadVersionAndInstallerFilenameChanges()
    {
        using var workspace = new TestWorkspace();
        var file = Path.Combine(workspace.Root, "profiles.json");
        var project = Project(workspace);
        var store = new PackagingProfileStore(file);
        var first = store.GetOrCreate(project);
        var updated = store.GetOrCreate(project with { Version = "2.0.0" });
        Assert.Equal(first.AppId, updated.AppId);
        Assert.Equal("2.0.0", updated.AppVersion);
        Assert.Contains("2.0.0", updated.InstallerFileName);
        store.Save(new PackagingOptions { Project = project, AppId = first.AppId, AppName = "自定义名称",
            AppVersion = "custom", InstallerFileName = "my installer.exe" });
        var reloaded = new PackagingProfileStore(file).GetOrCreate(project with { Version = "3.0.0" });
        Assert.Equal(first.AppId, reloaded.AppId);
        Assert.Equal("my installer.exe", reloaded.InstallerFileName);
        Assert.Equal("自定义名称", reloaded.AppName);
        Assert.Equal("custom", reloaded.AppVersion);
    }

    [Fact]
    public async Task ConcurrentStores_GetOneIdentityAndDoNotLoseOtherProjectProfiles()
    {
        using var workspace = new TestWorkspace();
        var file = Path.Combine(workspace.Root, "profiles.json");
        var project = Project(workspace);
        var stores = Enumerable.Range(0, 10).Select(_ => new PackagingProfileStore(file)).ToArray();
        var ids = await Task.WhenAll(stores.Select(store => Task.Run(() => store.GetOrCreate(project).AppId)));
        Assert.Single(ids.Distinct());
        var other = project with { ProjectPath = workspace.Write("second.csproj", "<Project/>"), ProductName = "Second" };
        var secondId = stores[0].GetOrCreate(other).AppId;
        stores[1].SaveToolsAndSource(new BuildTools { DotNetPath = "remembered.exe" }, "source.csproj");
        var reloaded = new PackagingProfileStore(file);
        Assert.Equal(ids[0], reloaded.GetOrCreate(project).AppId);
        Assert.Equal(secondId, reloaded.GetOrCreate(other).AppId);
        Assert.Equal("source.csproj", reloaded.LastSourcePath);
    }

    [Fact]
    public void BrokenProfile_IsNotOverwrittenWithANewUpgradeIdentity()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.Write("broken.json", "{broken}");
        Assert.Throws<IOException>(() => new PackagingProfileStore(file));
        Assert.Equal("{broken}", File.ReadAllText(file));
    }

    [Fact]
    public void ExtraResourcePreferences_ContainOnlyPathsNotFileContents()
    {
        using var workspace = new TestWorkspace();
        var file = Path.Combine(workspace.Root, "profiles.json");
        var license = workspace.Write("license.txt", "DO_NOT_SERIALIZE_THIS_LICENSE_CONTENT");
        var project = Project(workspace);
        var store = new PackagingProfileStore(file);
        var profile = store.GetOrCreate(project);
        store.Save(new PackagingOptions { Project = project, AppId = profile.AppId, AppName = "Test", AppVersion = "1.0.0",
            ExtraResources = [new(license, "license.txt")] });
        Assert.DoesNotContain("DO_NOT_SERIALIZE_THIS_LICENSE_CONTENT", File.ReadAllText(file));
        Assert.Single(new PackagingProfileStore(file).GetOrCreate(project).ExtraResources);
    }

    private static ProjectDescriptor Project(TestWorkspace workspace) => new()
    {
        ProjectPath = workspace.Write("source.csproj", "<Project/>"), ProductName = "Test App", Version = "1.0.0"
    };
}
