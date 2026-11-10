using System.IO;
using System.Text;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>清理审计日志（仿 PackagingService.RunLog 的语义）：CreateNew 独占创建、逐行带 "O" 时间戳、
/// 写入失败不静默（Check 抛出终止清理——审计不完整比少删更不可接受）；模拟运行同样完整写日志。</summary>
public class CleanupAuditLog : IDisposable
{
    /// <summary>审计保留份数。轮换只作用于本功能自有子树 cleaner\audit\cleanup_*.log。</summary>
    private const int RetainedRuns = 20;

    private StreamWriter? _writer;
    private readonly TimeProvider _timeProvider;

    private CleanupAuditLog(string filePath, StreamWriter writer, TimeProvider timeProvider)
    {
        Path = filePath;
        _writer = writer;
        _timeProvider = timeProvider;
    }

    /// <summary>测试替身入口：子类可覆盖 Write 模拟写入失败（审计中止路径的用例）。</summary>
    protected CleanupAuditLog(string filePath, StreamWriter writer)
        : this(filePath, writer, TimeProvider.System) { }

    public string Path { get; }
    public string? Error { get; private set; }

    public static string DefaultDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JLinkRTTViewer", "cleaner", "audit");

    /// <summary>创建本次运行的审计文件 cleanup_yyyyMMdd_HHmmss_毫秒.log，并轮换删除超过保留数的旧日志。
    /// 时钟经 TimeProvider 注入：文件名与行内时间戳使用同一时钟，固定时钟测试完全确定。</summary>
    public static CleanupAuditLog CreateNew(string? directory = null, DateTimeOffset? timestamp = null,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var target = directory ?? DefaultDirectory;
        Directory.CreateDirectory(target);
        var moment = timestamp ?? clock.GetLocalNow();
        var filePath = System.IO.Path.Combine(target,
            "cleanup_" + moment.ToString("yyyyMMdd_HHmmss_fff") + ".log");
        try
        {
            var writer = new StreamWriter(new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
            var log = new CleanupAuditLog(filePath, writer, clock);
            foreach (var failure in Rotate(target, filePath))
                log.Write($"rotation-warning 无法删除旧审计日志 \"{failure}\"");
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("无法创建清理审计日志：" + filePath, ex);
        }
    }

    /// <summary>逐行写入（自动加时间戳前缀）。失败记录到 Error，不在此抛出；由 Check 统一判定。</summary>
    public virtual void Write(string line)
    {
        if (_writer is null) return;
        try { _writer.WriteLine(_timeProvider.GetLocalNow().ToString("O") + " " + line); }
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

    /// <summary>轮换删除超过保留数的旧日志。返回删除失败（不可观测地残留）的路径，调用方写入警示行。</summary>
    private static List<string> Rotate(string directory, string keepPath)
    {
        var failures = new List<string>();
        try
        {
            var stale = Directory.GetFiles(directory, "cleanup_*.log")
                .Where(path => !path.Equals(keepPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Skip(RetainedRuns - 1);
            foreach (var path in stale)
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(path);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 轮换枚举失败不影响本次审计；下一次运行会再次尝试。记录 keepPath 之外无法给出的细节。
            failures.Add($"（枚举失败：{ex.Message}）");
        }
        return failures;
    }
}
