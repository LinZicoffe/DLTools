using System.IO;
using System.Text;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>清理审计日志（仿 PackagingService.RunLog 的语义）：CreateNew 独占创建、逐行带 "O" 时间戳、
/// 写入失败不静默（Check 抛出终止清理——审计不完整比少删更不可接受）；模拟运行同样完整写日志。</summary>
public sealed class CleanupAuditLog : IDisposable
{
    /// <summary>审计保留份数。轮换只作用于本功能自有子树 cleaner\audit\cleanup_*.log。</summary>
    private const int RetainedRuns = 20;

    private StreamWriter? _writer;

    private CleanupAuditLog(string filePath, StreamWriter writer)
    {
        Path = filePath;
        _writer = writer;
    }

    public string Path { get; }
    public string? Error { get; private set; }

    public static string DefaultDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JLinkRTTViewer", "cleaner", "audit");

    /// <summary>创建本次运行的审计文件 cleanup_yyyyMMdd_HHmmss_毫秒.log，并轮换删除超过保留数的旧日志。</summary>
    public static CleanupAuditLog CreateNew(string? directory = null, DateTimeOffset? timestamp = null)
    {
        var target = directory ?? DefaultDirectory;
        Directory.CreateDirectory(target);
        var moment = timestamp ?? DateTimeOffset.Now;
        var filePath = System.IO.Path.Combine(target,
            "cleanup_" + moment.ToString("yyyyMMdd_HHmmss_fff") + ".log");
        try
        {
            var writer = new StreamWriter(new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
            Rotate(target, filePath);
            return new CleanupAuditLog(filePath, writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("无法创建清理审计日志：" + filePath, ex);
        }
    }

    /// <summary>逐行写入（自动加时间戳前缀）。失败记录到 Error，不在此抛出；由 Check 统一判定。</summary>
    public void Write(string line)
    {
        if (_writer is null) return;
        try { _writer.WriteLine(DateTimeOffset.Now.ToString("O") + " " + line); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Error ??= ex.Message;
        }
    }

    /// <summary>审计写入一旦失败即中止：调用方在关键节点调用，抛出 IOException。</summary>
    public void Check()
    {
        if (Error is not null)
            throw new IOException("清理审计日志无法继续写入：" + Error);
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private static void Rotate(string directory, string keepPath)
    {
        try
        {
            var stale = Directory.GetFiles(directory, "cleanup_*.log")
                .Where(path => !path.Equals(keepPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Skip(RetainedRuns - 1);
            foreach (var path in stale)
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 轮换失败不影响本次审计；下一次运行会再次尝试。
        }
    }
}
