using System.IO;
using System.Text.Json;

namespace DLTools.Tests;

/// <summary>集成测试证据落盘：JSON 写 TestResults（不入 Git），供验收记录引用。</summary>
internal static class TestEvidence
{
    /// <summary>证据目录：优先环境变量（必须是绝对路径且不含 ..），否则落在输出目录的 TestResults 下。</summary>
    private static string ResolveEvidenceDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DLTOOLS_DOWNLOAD_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(configured)
            && !configured.Contains("..", StringComparison.Ordinal)
            && Path.IsPathRooted(configured))
            return Path.GetFullPath(configured);
        return Path.Combine(AppContext.BaseDirectory, "TestResults");
    }

    public static void Save(string name, object payload)
    {
        var directory = ResolveEvidenceDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"高速下载-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}