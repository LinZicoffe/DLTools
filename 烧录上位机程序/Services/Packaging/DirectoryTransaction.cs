using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

/// <summary>A durable recovery barrier around one build-output directory. Never deletes a tree.</summary>
public sealed class DirectoryTransaction
{
    private readonly RecoveryRegistry _registry;
    private readonly string _journal;
    private readonly string _failedDirectory;
    private readonly List<string> _preservedPaths = [];
    private bool _finished;
    private bool _outputPreserved;
    private bool _originalRestored;
    public string TargetDirectory { get; }
    public string? BackupDirectory { get; }
    public IReadOnlyList<string> PreservedPaths => _preservedPaths.AsReadOnly();

    private DirectoryTransaction(string target, string? backup, string failed, RecoveryRegistry registry, string journal)
    {
        TargetDirectory = target;
        BackupDirectory = backup;
        _failedDirectory = failed;
        _registry = registry;
        _journal = journal;
        if (backup is not null) _preservedPaths.Add(backup);
    }

    public static DirectoryTransaction Begin(string target, ProjectDescriptor project, string backupRoot,
        RecoveryRegistry registry, bool replacementConfirmed = false)
    {
        target = PackagingFileSystem.ValidateMutableDirectory(target, project);
        backupRoot = PackagingFileSystem.ValidateMutableDirectory(backupRoot, project);
        PackagingFileSystem.EnsureDisjoint(target, backupRoot);
        var projectDirectory = PackagingFileSystem.FullPath(Path.GetDirectoryName(project.ProjectPath)!);
        if (PackagingFileSystem.IsWithin(backupRoot, projectDirectory))
            throw new IOException($"备份目录不能位于项目编译源码范围内：{backupRoot}");
        if (!string.Equals(Path.GetPathRoot(target), Path.GetPathRoot(backupRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份目录必须与待替换目录位于同一磁盘卷。");
        registry.ValidateLocation(target, backupRoot);
        registry.EnsureUnblocked(target);
        var existed = Directory.Exists(target);
        if (existed && Directory.EnumerateFileSystemEntries(target).Any() && !replacementConfirmed)
            throw new IOException($"目录非空，需要确认备份后才能替换：{target}");
        var token = Guid.NewGuid().ToString("N");
        var backup = existed ? Path.Combine(backupRoot, "backup-" + token) : null;
        var failed = Path.Combine(backupRoot, "failed-" + token);
        // Registration atomically checks target/backup/failed overlap against every pending journal under a cross-process mutex.
        var journal = registry.Register(target, backup, failed);
        var transaction = new DirectoryTransaction(target, backup, failed, registry, journal);
        var moved = false;
        try
        {
            PackagingFileSystem.EnsureNoLinks(backupRoot);
            Directory.CreateDirectory(backupRoot);
            PackagingFileSystem.EnsureNoLinks(target);
            if (existed)
            {
                Directory.Move(target, backup!);
                moved = true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!CreateDirectoryExclusive(target, IntPtr.Zero))
                throw new IOException($"无法创建独占构建输出目录：{target}", new Win32Exception(Marshal.GetLastWin32Error()));
            return transaction;
        }
        catch (Exception original)
        {
            // A failed Move leaves the original target untouched; do not move it away during cleanup.
            if (!moved)
            {
                try { registry.Complete(journal); }
                catch (Exception cleanup) { throw new AggregateException($"开始事务失败，恢复记录保留：{journal}", original, cleanup); }
            }
            else
            {
                try { transaction.Rollback(); }
                catch (Exception recovery) { throw new AggregateException($"开始事务失败且恢复未完成：{journal}", original, recovery); }
            }
            throw;
        }
    }

    public void Rollback()
    {
        if (_finished) return;
        try
        {
            PackagingFileSystem.EnsureNoLinks(TargetDirectory);
            PackagingFileSystem.EnsureNoLinks(_failedDirectory);
            if (BackupDirectory is not null) PackagingFileSystem.EnsureNoLinks(BackupDirectory);
            if (File.Exists(TargetDirectory)) throw new IOException($"恢复路径被文件占用：{TargetDirectory}");
            if (!_outputPreserved && Directory.Exists(TargetDirectory))
            {
                Directory.Move(TargetDirectory, _failedDirectory);
                _preservedPaths.Add(_failedDirectory);
                _outputPreserved = true;
            }
            _outputPreserved = true;
            if (BackupDirectory is not null && !_originalRestored)
            {
                Directory.Move(BackupDirectory, TargetDirectory);
                _preservedPaths.Remove(BackupDirectory);
                _originalRestored = true;
            }
            _registry.Complete(_journal);
            _finished = true;
        }
        catch (Exception error)
        {
            throw new IOException($"目录恢复失败。原路径：{TargetDirectory}；备份：{BackupDirectory ?? "无"}；本次输出：{_failedDirectory}；恢复记录：{_journal}。请保留这些位置，人工恢复后才可重试。", error);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryExclusive(string path, IntPtr securityAttributes);

    public string? Commit()
    {
        if (_finished) return null;
        _finished = true;
        try { _registry.Complete(_journal); return null; }
        catch (Exception error) { return $"安装包已保存，但无法清理恢复记录：{_journal}；备份：{BackupDirectory ?? "无"}。{error.Message}"; }
    }
}




