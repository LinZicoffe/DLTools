using System.Security.Cryptography;
using System.Text.Json;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.Authorization;

namespace OfflineAuthorization.Demo.Core;

/// <summary>PUBLIC TEST FIXTURE. Never provision these credentials to a real customer.</summary>
public static class DemoProfile
{
    public const string DeviceId = "11112222333344445555666677778888";
    public const string DisplayName = "DEMO公开测试设备（禁止生产使用）";
    public const string BackupPassword = "DLTools-Demo-Only-2026!";
    internal static byte[] CreatePublicKey() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    public static byte[] CreateBackup()
    {
        var key = CreatePublicKey();
        byte[]? plaintext = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                Profiles = new[] { new { ProtocolVersion = 1, DeviceId, DisplayName,
                    CreatedUtc = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), Key = key } }
            });
            return AuthorizationBackupService.Encrypt(plaintext, BackupPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static async Task ExportAsync(string path)
    {
        if (!BackgroundTaskHost.Shared.TryStart(BackgroundOperationKind.Authorization, token =>
        {
            var bytes = CreateBackup();
            try { AtomicFile.Write(path, bytes, token); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return Task.CompletedTask;
        }, out var task))
            throw new InvalidOperationException("测试档案正在导出，请稍后重试。");
        await task;
    }
}
