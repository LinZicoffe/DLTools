using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class CrossVolumePackagingFactAttribute : FactAttribute
{
    public CrossVolumePackagingFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLTOOLS_INTEGRATION") != "1")
            Skip = "设置 DLTOOLS_INTEGRATION=1 执行跨卷提交验证。";
        else if (Path.GetPathRoot(Path.GetTempPath()) == Path.GetPathRoot(AppContext.BaseDirectory))
            Skip = "实际跨卷验证需要临时目录与测试构建目录位于不同卷。";
    }
}

public class AtomicInstallerCrossVolumeTests
{
    [CrossVolumePackagingFact]
    public async Task SourceOnTempVolume_CommitsAndReplacesOnBuildVolume()
    {
        using var source = new TestWorkspace();
        using var destination = new TestWorkspace(AppContext.BaseDirectory);
        Assert.NotEqual(Path.GetPathRoot(source.Root), Path.GetPathRoot(destination.Root));
        var payload = source.Write("compiled.exe", new string('A', 100000));
        var first = await AtomicInstallerWriter.CommitAsync(payload, destination.Root, "跨卷安装包.exe", _ => Task.FromResult(true));
        Assert.Equal(File.ReadAllBytes(payload), File.ReadAllBytes(first));
        File.WriteAllText(payload, "updated");
        var replaced = await AtomicInstallerWriter.CommitAsync(payload, destination.Root, "跨卷安装包.exe", _ => Task.FromResult(true));
        Assert.Equal(first, replaced);
        Assert.Equal("updated", File.ReadAllText(replaced));
        Assert.Empty(Directory.EnumerateFiles(destination.Root, "*.pending"));
    }
}
