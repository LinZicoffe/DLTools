using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 烧录上位机程序.Models.Authorization;

namespace 烧录上位机程序.Services.Authorization;

/// <summary>Reloads under a process-wide named mutex. All stored document data is DPAPI encrypted.</summary>
public sealed class AuthorizationProfileStore
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("DLTOOLS-AUTH-STORE|1");
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    public string FilePath { get; }

    public AuthorizationProfileStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JLinkRTTViewer", "authorization", "profiles.dat"));

    public IReadOnlyList<AuthorizationProfile> Load()
    {
        using var held = AcquireLock();
        using var document = ReadDocument(ReadRaw());
        return Metadata(document);
    }

    public AuthorizationProfile Create(string name)
    {
        name = ValidateName(name);
        using var held = AcquireLock();
        using var document = ReadDocument(ReadRaw());
        if (document.Profiles.Count >= 5000) throw new InvalidOperationException("档案数量已达上限（5000）。");
        var entry = new AuthorizationEntry { DeviceId = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            DisplayName = name, Key = RandomNumberGenerator.GetBytes(32), CreatedUtc = DateTimeOffset.UtcNow };
        document.Profiles.Add(entry);
        Persist(document, CancellationToken.None);
        return entry.Metadata;
    }

    public void Rename(string deviceId, string name)
    {
        name = ValidateName(name);
        using var held = AcquireLock();
        using var document = ReadDocument(ReadRaw());
        Find(document, deviceId).DisplayName = name;
        Persist(document, CancellationToken.None);
    }

    public string Generate(string deviceId, string challenge)
    {
        using var held = AcquireLock();
        using var document = ReadDocument(ReadRaw());
        var entry = Find(document, deviceId);
        return OfflineAuthorizationService.Generate(entry.Key, entry.DeviceId, challenge);
    }

    public byte[] CreateBackup(string password)
    {
        AuthorizationBackupService.ValidatePassword(password);
        byte[] plaintext;
        using (var held = AcquireLock())
        using (var document = ReadDocument(ReadRaw())) plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try { return AuthorizationBackupService.Encrypt(plaintext, password); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>Authentication and merge validation finish before any write or confirmation.</summary>
    public AuthorizationRestorePlan PrepareRestore(byte[] backup, string password, bool recoverUnreadableStore = false)
    {
        var plaintext = AuthorizationBackupService.Decrypt(backup, password);
        try
        {
            using var incoming = ParseDocument(plaintext);
            using var held = AcquireLock();
            var raw = ReadRaw();
            AuthorizationDocument current;
            var recovery = false;
            try { current = ReadDocument(raw); }
            catch (InvalidDataException) when (recoverUnreadableStore && raw is not null)
            {
                current = new();
                recovery = true;
            }
            try
            {
                var added = 0;
                var renamed = 0;
                foreach (var entry in incoming.Profiles)
                {
                    var existing = current.Profiles.FirstOrDefault(p => p.DeviceId == entry.DeviceId);
                    if (existing is not null)
                    {
                        if (!CryptographicOperations.FixedTimeEquals(existing.Key, entry.Key))
                            throw new InvalidDataException("同一设备标识存在不同密钥，不能覆盖。请保留原库并核对备份来源。");
                        if (existing.DisplayName != entry.DisplayName) renamed++;
                        existing.DisplayName = entry.DisplayName;
                    }
                    else
                    {
                        current.Profiles.Add(new AuthorizationEntry { DeviceId = entry.DeviceId, DisplayName = entry.DisplayName,
                            CreatedUtc = entry.CreatedUtc, Key = (byte[])entry.Key.Clone() });
                        added++;
                    }
                }
                ValidateDocument(current);
                return new AuthorizationRestorePlan(FilePath, raw is null ? null : SHA256.HashData(raw), current, added, renamed, recovery);
            }
            catch { current.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <returns>Preserved damaged file path, if recovering an unreadable store.</returns>
    public string? CommitRestore(AuthorizationRestorePlan plan, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(FilePath, plan.StorePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("恢复计划不属于当前档案库。");
        using var held = AcquireLock();
        var document = plan.GetDocument();
        var raw = ReadRaw();
        var fingerprint = raw is null ? null : SHA256.HashData(raw);
        if (!(fingerprint is null && plan.Fingerprint is null) &&
            (fingerprint is null || plan.Fingerprint is null || !CryptographicOperations.FixedTimeEquals(fingerprint, plan.Fingerprint)))
            throw new IOException("确认期间档案库已被其他实例修改，请重新预览恢复。");
        cancellationToken.ThrowIfCancellationRequested();
        string? preserved = null;
        if (plan.RecoverUnreadable && raw is not null)
        {
            preserved = FilePath + ".damaged-" + Guid.NewGuid().ToString("N");
            WriteAtomic(preserved, raw, cancellationToken);
        }
        Persist(document, cancellationToken);
        plan.MarkCommitted();
        return preserved;
    }

    public void SaveBackup(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (string.Equals(Path.GetFullPath(path), FilePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("备份不能覆盖当前档案库。");
        if (bytes.Length > AuthorizationBackupService.MaxFileBytes) throw new InvalidDataException("备份文件过大。");
        WriteAtomic(path, bytes, cancellationToken);
    }

    public static byte[] ReadBackup(string path) => ReadBounded(path, AuthorizationBackupService.MaxFileBytes);

    private byte[]? ReadRaw()
    {
        try { return ReadBounded(FilePath, AuthorizationBackupService.MaxPayloadBytes + 4096); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("档案或备份文件过大，已拒绝读取。");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static AuthorizationDocument ReadDocument(byte[]? encrypted)
    {
        if (encrypted is null) return new();
        byte[] plaintext;
        try { plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new InvalidDataException("授权档案无法解密，请保留原文件并从加密备份恢复；不会自动重建密钥。"); }
        try { return ParseDocument(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static AuthorizationDocument ParseDocument(byte[] plaintext)
    {
        AuthorizationDocument? document = null;
        try
        {
            if (plaintext.Length > AuthorizationBackupService.MaxPayloadBytes) throw new InvalidDataException("档案数据过大。");
            document = JsonSerializer.Deserialize<AuthorizationDocument>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("授权档案为空。");
            ValidateDocument(document);
            return document;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
        {
            document?.Dispose();
            throw new InvalidDataException("授权档案格式无效或版本不受支持，请保留原文件并恢复备份。");
        }
    }

    private static void ValidateDocument(AuthorizationDocument document)
    {
        if (document.SchemaVersion != 1 || document.Profiles is null || document.Profiles.Count > 5000)
            throw new InvalidDataException("档案版本或数量无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Profiles)
        {
            if (entry is null || entry.ProtocolVersion != 1 || entry.Key is null || entry.Key.Length != 32
                || entry.DeviceId != OfflineAuthorizationService.NormalizeDeviceId(entry.DeviceId)
                || entry.DisplayName != ValidateName(entry.DisplayName) || !ids.Add(entry.DeviceId)
                || entry.CreatedUtc == default)
                throw new InvalidDataException("档案内容无效。");
        }
    }

    private static string ValidateName(string name)
    {
        var value = name?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 100 || value.Any(char.IsControl))
            throw new ArgumentException("客户／设备名称需为 1～100 个字符，不能包含控制字符。");
        return value;
    }

    private static AuthorizationEntry Find(AuthorizationDocument document, string id)
    {
        id = OfflineAuthorizationService.NormalizeDeviceId(id);
        return document.Profiles.FirstOrDefault(p => p.DeviceId == id)
            ?? throw new InvalidOperationException("设备档案不存在，请重新加载；不会自动创建替代密钥。");
    }

    private void Persist(AuthorizationDocument document, CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            if (plaintext.Length > AuthorizationBackupService.MaxPayloadBytes) throw new InvalidDataException("档案库过大。");
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            WriteAtomic(FilePath, encrypted, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static IReadOnlyList<AuthorizationProfile> Metadata(AuthorizationDocument document) =>
        document.Profiles.Select(p => p.Metadata).OrderBy(p => p.DisplayName, StringComparer.CurrentCulture).ToArray();

    private IDisposable AcquireLock()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())));
        var mutex = new Mutex(false, @"Global\DLToolsAuthorization_" + hash);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("其他实例正在操作授权档案，请稍后重试。");
            return new HeldMutex(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private static void WriteAtomic(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不能覆盖链接文件。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            // Atomic rename is the commit point: cancellation after this must not be reported as rollback.
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class HeldMutex(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}

internal sealed class AuthorizationDocument : IDisposable
{
    public int SchemaVersion { get; set; } = 1;
    public List<AuthorizationEntry> Profiles { get; set; } = [];
    public void Dispose()
    {
        if (Profiles is null) return;
        foreach (var entry in Profiles)
            if (entry?.Key is { } key) CryptographicOperations.ZeroMemory(key);
    }
}

internal sealed class AuthorizationEntry
{
    public int ProtocolVersion { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public byte[] Key { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public AuthorizationProfile Metadata => new(DeviceId, DisplayName, CreatedUtc);
}

public sealed class AuthorizationRestorePlan : IDisposable
{
    private AuthorizationDocument? _document;
    internal string StorePath { get; }
    internal byte[]? Fingerprint { get; }
    internal bool RecoverUnreadable { get; }
    public int NewProfiles { get; }
    public int RenamedProfiles { get; }
    public bool PreservesDamagedFile => RecoverUnreadable;
    public IReadOnlyList<AuthorizationProfile> Profiles { get; }

    internal AuthorizationRestorePlan(string storePath, byte[]? fingerprint, AuthorizationDocument document,
        int newProfiles, int renamedProfiles, bool recovery)
    {
        StorePath = storePath;
        Fingerprint = fingerprint;
        _document = document;
        Profiles = document.Profiles.Select(p => p.Metadata).ToArray();
        NewProfiles = newProfiles;
        RenamedProfiles = renamedProfiles;
        RecoverUnreadable = recovery;
    }

    internal AuthorizationDocument GetDocument() => _document ?? throw new InvalidOperationException("恢复计划已结束，请重新预览。");
    internal void MarkCommitted() => Dispose();
    public void Dispose() { _document?.Dispose(); _document = null; }
}
