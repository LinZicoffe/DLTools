using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 烧录上位机程序.Services.Packaging;

public sealed class RecoveryRegistry
{
    private readonly string _stateDirectory;
    private readonly string _mutexName;

    public RecoveryRegistry(string? stateDirectory = null)
    {
        _stateDirectory = PackagingFileSystem.FullPath(stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JLinkRTTViewer", "packaging", "recovery"));
        _mutexName = @"Global\DLTools.PackagingRecovery." + HashPath(_stateDirectory);
    }

    internal void ValidateLocation(string target, string backupRoot)
    {
        PackagingFileSystem.EnsureDisjoint(target, _stateDirectory);
        PackagingFileSystem.EnsureDisjoint(backupRoot, _stateDirectory);
    }

    private static string HashPath(string path) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(PackagingFileSystem.FullPath(path).ToUpperInvariant())));

    internal string JournalPath(string target) => Path.Combine(_stateDirectory, HashPath(target) + ".json");

    public void EnsureUnblocked(string target)
    {
        target = PackagingFileSystem.FullPath(target);
        WithLock(() =>
        {
            EnsurePathsUnblocked([target]);
            return true;
        });
    }

    private sealed record PendingRecovery(string TargetDirectory, string? BackupDirectory, string FailedDirectory)
    {
        public IEnumerable<string> Paths => BackupDirectory is null
            ? [TargetDirectory, FailedDirectory]
            : [TargetDirectory, BackupDirectory, FailedDirectory];
    }

    private void EnsurePathsUnblocked(IReadOnlyList<string> proposedPaths)
    {
        PackagingFileSystem.EnsureNoLinks(_stateDirectory);
        if (!Directory.Exists(_stateDirectory)) return;
        foreach (var journal in Directory.EnumerateFiles(_stateDirectory, "*.json"))
        {
            PendingRecovery pending;
            try
            {
                PackagingFileSystem.EnsureNoLinks(journal);
                using var document = JsonDocument.Parse(File.ReadAllText(journal));
                var root = document.RootElement;
                var target = ReadPath(root, "TargetDirectory", required: true)!;
                var backup = ReadPath(root, "BackupDirectory", required: false);
                var failed = ReadPath(root, "FailedDirectory", required: true)!;
                pending = new(target, backup, failed);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Corrupt/unreadable records cannot prove that any new target is safe.
                throw new IOException($"恢复记录无法完整读取，已阻止新事务。请先人工核对恢复记录：{journal}", error);
            }
            foreach (var proposed in proposedPaths)
            {
                if (pending.Paths.Any(path => PackagingFileSystem.IsWithin(proposed, path) || PackagingFileSystem.IsWithin(path, proposed)))
                    throw new IOException($"目录与未完成恢复事务重叠，须先人工恢复并核对记录：{journal}\n目标：{proposed}\n待恢复目标：{pending.TargetDirectory}；备份：{pending.BackupDirectory ?? "无"}；本次输出保留位置：{pending.FailedDirectory}");
            }
        }
    }

    private static string? ReadPath(JsonElement root, string property, bool required)
    {
        if (!root.TryGetProperty(property, out var value)) throw new JsonException($"缺少恢复字段：{property}");
        if (!required && value.ValueKind == JsonValueKind.Null) return null;
        var path = value.GetString();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new JsonException($"恢复字段不是绝对路径：{property}");
        return PackagingFileSystem.FullPath(path);
    }

    internal string Register(string target, string? backup, string failed)
    {
        target = PackagingFileSystem.FullPath(target);
        backup = backup is null ? null : PackagingFileSystem.FullPath(backup);
        failed = PackagingFileSystem.FullPath(failed);
        return WithLock(() =>
        {
            // Only the unique backup/failed paths participate: their shared container may serve disjoint jobs.
            var proposed = new PendingRecovery(target, backup, failed);
            EnsurePathsUnblocked(proposed.Paths.ToArray());
            PackagingFileSystem.EnsureNoLinks(_stateDirectory);
            Directory.CreateDirectory(_stateDirectory);
            var journal = JournalPath(target);
            using var file = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, new { proposed.TargetDirectory, proposed.BackupDirectory, proposed.FailedDirectory, CreatedUtc = DateTime.UtcNow });
            file.Flush(flushToDisk: true);
            return journal;
        });
    }

    internal void Complete(string journal)
    {
        WithLock(() =>
        {
            journal = PackagingFileSystem.FullPath(journal);
            if (!string.Equals(Path.GetDirectoryName(journal), _stateDirectory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复记录不在当前恢复目录中。");
            PackagingFileSystem.EnsureNoLinks(journal);
            File.Delete(journal);
            return true;
        });
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException($"恢复记录正在被另一进程使用，请稍后重试：{_stateDirectory}");
            return action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
