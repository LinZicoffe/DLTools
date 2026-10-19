using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class ProcessPressureTests
{
    [Fact]
    public async Task RunAsync_ConcurrentOutputLargerThanPipeBuffers_DrainsBothStreams()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new ExternalProcessRunner().RunAsync(ProcessFixture.Helper(), ["flood", "4096"],
            Environment.CurrentDirectory, cancellationToken: timeout.Token);
        Assert.Equal(0, result.ExitCode);
        var output = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var error = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4096, output.Length);
        Assert.Equal(4096, error.Length);
        for (var i = 0; i < 4096; i++)
        {
            Assert.Equal("out:" + i + ":" + new string('界', 256), output[i].TrimEnd('\r'));
            Assert.Equal("err:" + i + ":" + new string('文', 256), error[i].TrimEnd('\r'));
        }
    }
}

internal static class ProcessFixture
{
    public static string Helper()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "ProcessTestHost")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar)
            .FirstOrDefault(part => part is "Release" or "Debug") ?? "Release";
        var path = Path.Combine(directory.FullName, "tests", "ProcessTestHost", "bin", configuration,
            "net8.0", "ProcessTestHost.exe");
        Assert.True(File.Exists(path), path);
        return path;
    }
}
