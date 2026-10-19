using System.IO;
using System.Security.Cryptography;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

public static class AtomicInstallerWriter
{
    public static async Task<string> CommitAsync(string sourceFile, string outputDirectory, string installerFileName,
        Func<PackagingConfirmation, Task<bool>> confirm, CancellationToken cancellationToken = default)
    {
        var name = PackagingFileSystem.NormalizeInstallerFileName(installerFileName);
        var output = PackagingFileSystem.FullPath(outputDirectory);
        PackagingFileSystem.EnsureNoLinks(output);
        PackagingFileSystem.EnsureNoLinks(sourceFile);
        var target = Path.Combine(output, name);
        PackagingFileSystem.EnsureNoLinks(target);
        if (Directory.Exists(target)) throw new IOException($"安装包路径已被目录占用：{target}");
        var original = File.Exists(target) ? await FingerprintAsync(target, cancellationToken) : null;
        if (original is not null && !await confirm(new PackagingConfirmation("覆盖安装包", "输出目录已有同名安装包，是否覆盖？", target)))
            throw new OperationCanceledException("用户取消覆盖安装包。", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        var token = Guid.NewGuid().ToString("N");
        var pending = Path.Combine(output, ".dltools-" + token + ".pending");
        var replaced = Path.Combine(output, ".dltools-" + token + ".previous");
        try
        {
            // Always copy to the destination volume, even when compiler output is on another drive.
            await using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var destination = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                if (source.Length == 0) throw new IOException("编译器生成了空安装包。");
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }
            var sourceHash = await HashAsync(sourceFile, cancellationToken);
            var pendingHash = await HashAsync(pending, cancellationToken);
            if (sourceHash != pendingHash) throw new IOException("安装包复制校验失败，原输出未替换。");
            cancellationToken.ThrowIfCancellationRequested();
            PackagingFileSystem.EnsureNoLinks(output);
            PackagingFileSystem.EnsureNoLinks(target);
            if (original is null)
            {
                // No overwrite: a target appearing after initial inspection is an unconfirmed file.
                File.Move(pending, target, overwrite: false);
            }
            else
            {
                if (!File.Exists(target) || original != await FingerprintAsync(target, cancellationToken))
                    throw new IOException("确认后安装包发生变化，已停止覆盖。请重新确认。");
                cancellationToken.ThrowIfCancellationRequested();
                // Backup the actual replaced file so a race at Replace is detected rather than silently accepted.
                File.Replace(pending, target, replaced, ignoreMetadataErrors: false);
                Fingerprint actual;
                try { actual = await FingerprintAsync(replaced, CancellationToken.None); }
                catch (Exception verification)
                {
                    try { File.Replace(replaced, target, pending, ignoreMetadataErrors: false); }
                    catch (Exception recovery)
                    {
                        throw new AggregateException($"最终替换校验失败且旧包恢复失败。请保留：{target}；{replaced}", verification, recovery);
                    }
                    throw;
                }
                if (actual != original)
                {
                    File.Replace(replaced, target, pending, ignoreMetadataErrors: false);
                    throw new IOException("最终提交时安装包发生变化，已恢复发生变化的文件。请重新确认。");
                }
                try { File.Delete(replaced); }
                catch (IOException) { /* Package is already committed; retain the uniquely named previous file. */ }
                catch (UnauthorizedAccessException) { /* Cleanup cannot turn a successful commit into a failure. */ }
            }
            return target;
        }
        finally
        {
            // Only this invocation's temporary file is removed; recovery backups survive unexpected failures.
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (IOException) { /* Preserve a locked temporary rather than mask the operation result. */ }
            catch (UnauthorizedAccessException) { /* The file is exclusively named for this attempt. */ }
        }
    }

    private sealed record Fingerprint(long Length, DateTime LastWriteUtc, string Hash);
    private static async Task<Fingerprint> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        PackagingFileSystem.EnsureNoLinks(path);
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc, await HashAsync(path, cancellationToken));
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
    }
}



