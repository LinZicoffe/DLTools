using System.Diagnostics;
using Xunit;

namespace DLTools.Tests;

/// <summary>测试用 junction 创建（mklink /J 无需管理员/开发者模式）。
/// 注意：DoS 设备路径攻击面与 symlink 不同，junction 只能指向本地目录，够覆盖清理引擎的威胁模型。</summary>
internal static class TestJunction
{
    public static void Create(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(15));
        Assert.True(Directory.Exists(linkPath), $"junction 创建失败：{linkPath} -> {targetPath}");
    }
}
