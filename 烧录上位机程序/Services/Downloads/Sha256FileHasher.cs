using System.IO;
using System.Security.Cryptography;

namespace 烧录上位机程序.Services.Downloads;

public static class Sha256FileHasher
{
    /// <summary>流式计算整个文件的 SHA-256，返回小写十六进制串。</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
