using 烧录上位机程序.Services.Cleanup;

namespace DLTools.Tests;

/// <summary>C盘清理测试共享夹具：假环境（全部位于 GUID 临时根内）、固定时钟、文件年龄工具。</summary>
internal static class DiskCleanupTestHelpers
{
    public static CleanupEnvironment CreateEnvironment(TestWorkspace workspace)
    {
        var profile = Path.Combine(workspace.Root, "profile");
        var local = Path.Combine(profile, "AppData", "Local");
        var roaming = Path.Combine(profile, "AppData", "Roaming");
        var temp = Path.Combine(workspace.Root, "temp");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(temp);
        return new CleanupEnvironment
        {
            Temp = temp,
            LocalAppData = local,
            RoamingAppData = roaming,
            UserProfile = profile,
            RejectedRoots =
            [
                Path.Combine(profile, "Desktop"),
                Path.Combine(profile, "Documents"),
                Path.Combine(profile, "Downloads")
            ],
            ProtectedPaths =
            [
                Path.Combine(profile, ".zcode", "cli", "memories"),
                Path.Combine(profile, ".zcode", "cli", "config.json")
            ]
        };
    }

    public static string WriteFile(string path, long bytes = 32)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>把文件写入/创建时间整体拨到 now - age，模拟历史文件。</summary>
    public static void SetAge(string path, DateTimeOffset now, TimeSpan age)
    {
        File.SetLastWriteTimeUtc(path, now.UtcDateTime - age);
        File.SetCreationTimeUtc(path, now.UtcDateTime - age);
    }

    public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
