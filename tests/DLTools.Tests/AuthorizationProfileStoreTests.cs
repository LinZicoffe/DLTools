using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using 烧录上位机程序.Services.Authorization;

namespace DLTools.Tests;

public class AuthorizationProfileStoreTests
{
    private const string Password = "Offline-Test-Backup-Password!2026";

    [Fact]
    public void RenameAndReload_PreserveIdentityAndResponse_AndDiskContainsNoPlaintext()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, "one");
        var profile = store.Create("现场 A 的测试档案");
        var response = store.Generate(profile.DeviceId, "000012");
        store.Rename(profile.DeviceId, "新名称");
        var reloaded = new AuthorizationProfileStore(store.FilePath);
        Assert.Equal(profile.DeviceId, Assert.Single(reloaded.Load()).DeviceId);
        Assert.Equal("新名称", reloaded.Load()[0].DisplayName);
        Assert.Equal(response, reloaded.Generate(profile.DeviceId, "000012"));
        var disk = File.ReadAllBytes(store.FilePath);
        Assert.DoesNotContain(profile.DeviceId, Encoding.UTF8.GetString(disk));
        Assert.DoesNotContain("新名称", Encoding.UTF8.GetString(disk));
        Assert.Throws<InvalidOperationException>(() => store.Generate(Guid.NewGuid().ToString("N"), "000012"));
    }

    [Fact]
    public async Task ConcurrentStores_PreserveAllDeviceProfiles()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, "concurrent");
        var created = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            new AuthorizationProfileStore(store.FilePath).Create("设备 " + index))));
        Assert.Equal(12, store.Load().Count);
        Assert.Equal(12, created.Select(p => p.DeviceId).Distinct().Count());
    }

    [Fact]
    public void CorruptStore_IsPreservedAndNeverSilentlyRekeyed()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.Write("broken.dat", "NOT_A_DPAPI_DOCUMENT");
        var store = new AuthorizationProfileStore(file);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Create("不可覆盖"));
        Assert.Equal("NOT_A_DPAPI_DOCUMENT", File.ReadAllText(file));
    }

    [Fact]
    public void Backup_RestoresOnAnotherStoreAndKeepsCurrentDevices()
    {
        using var workspace = new TestWorkspace();
        var source = Store(workspace, "source");
        var original = source.Create("客户 A");
        var expected = source.Generate(original.DeviceId, "123456");
        var backup = source.CreateBackup(Password);
        Assert.DoesNotContain(original.DeviceId, Encoding.UTF8.GetString(backup));
        var target = Store(workspace, "target");
        var other = target.Create("客户 B");
        using var plan = target.PrepareRestore(backup, Password);
        Assert.Equal(1, plan.NewProfiles);
        Assert.Equal(2, plan.Profiles.Count);
        target.CommitRestore(plan);
        Assert.Equal(expected, target.Generate(original.DeviceId, "123456"));
        Assert.Contains(target.Load(), p => p.DeviceId == other.DeviceId);
        Assert.Throws<InvalidOperationException>(() => target.CommitRestore(plan));
    }

    [Fact]
    public void Restore_RenamesExistingDeviceWithoutChangingItsSecret()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, "rename");
        var profile = store.Create("备份时名称");
        var expected = store.Generate(profile.DeviceId, "654321");
        var backup = store.CreateBackup(Password);
        store.Rename(profile.DeviceId, "后来名称");
        using var plan = store.PrepareRestore(backup, Password);
        Assert.Equal(0, plan.NewProfiles);
        Assert.Equal(1, plan.RenamedProfiles);
        store.CommitRestore(plan);
        Assert.Equal("备份时名称", Assert.Single(store.Load()).DisplayName);
        Assert.Equal(expected, store.Generate(profile.DeviceId, "654321"));
    }

    [Fact]
    public void WrongPasswordTamperingAndUnsupportedParameters_NeverChangeStore()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, "errors");
        store.Create("原始档案");
        var backup = store.CreateBackup(Password);
        var before = File.ReadAllBytes(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.PrepareRestore(backup, "A-Different-Password!"));
        var changed = (byte[])backup.Clone();
        changed[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => store.PrepareRestore(changed, Password));
        changed = (byte[])backup.Clone();
        changed[8] ^= 1; // Reject malicious KDF work factors before deriving a key.
        Assert.Throws<InvalidDataException>(() => store.PrepareRestore(changed, Password));
        Assert.Throws<ArgumentException>(() => store.CreateBackup("123456"));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void Restore_RejectsAStaleConfirmationInsteadOfOverwritingNewChanges()
    {
        using var workspace = new TestWorkspace();
        var source = Store(workspace, "source");
        source.Create("待恢复");
        var target = Store(workspace, "target");
        using var plan = target.PrepareRestore(source.CreateBackup(Password), Password);
        var concurrentlyAdded = new AuthorizationProfileStore(target.FilePath).Create("确认期间新增");
        Assert.Throws<IOException>(() => target.CommitRestore(plan));
        Assert.Equal(concurrentlyAdded.DeviceId, Assert.Single(target.Load()).DeviceId);
    }

    [Fact]
    public void CancelBeforeCommit_LeavesCurrentStoreAndBackupIntact()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, "cancel");
        store.Create("原始档案");
        var backup = store.CreateBackup(Password);
        using var plan = store.PrepareRestore(backup, Password);
        var before = File.ReadAllBytes(store.FilePath);
        var backupFile = workspace.Write("existing.dlauth", "OLD_BACKUP");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.CommitRestore(plan, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.SaveBackup(backupFile, backup, cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        Assert.Equal("OLD_BACKUP", File.ReadAllText(backupFile));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void RecoverUnreadableStore_PreservesDamagedBytesBeforeAtomicReplacement()
    {
        using var workspace = new TestWorkspace();
        var source = Store(workspace, "source");
        var original = source.Create("恢复档案");
        var backup = source.CreateBackup(Password);
        var damaged = workspace.Write("damaged.dat", "KEEP_THIS_DAMAGED_FILE");
        var target = new AuthorizationProfileStore(damaged);
        Assert.Throws<InvalidDataException>(() => target.PrepareRestore(backup, Password));
        using var plan = target.PrepareRestore(backup, Password, recoverUnreadableStore: true);
        Assert.True(plan.PreservesDamagedFile);
        var preservedPath = target.CommitRestore(plan);
        Assert.Equal("KEEP_THIS_DAMAGED_FILE", File.ReadAllText(preservedPath!));
        Assert.Equal(original.DeviceId, Assert.Single(target.Load()).DeviceId);
    }

    [Fact]
    public void BackupCannotOverwriteStore_AndOversizedFilesAreRejected()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, "bounded");
        store.Create("原始档案");
        var before = File.ReadAllBytes(store.FilePath);
        var backup = store.CreateBackup(Password);
        Assert.Throws<ArgumentException>(() => store.SaveBackup(store.FilePath, backup));
        var largePath = Path.Combine(workspace.Root, "oversized.dlauth");
        using (var file = File.Create(largePath)) file.SetLength(5 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => AuthorizationProfileStore.ReadBackup(largePath));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void ConflictingSecretsForSameDevice_AreNeverOverwritten()
    {
        using var workspace = new TestWorkspace();
        var id = "11112222333344445555666677778888";
        var source = Store(workspace, "source");
        var target = Store(workspace, "target");
        WritePublicFixture(source.FilePath, id, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        WritePublicFixture(target.FilePath, id, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var before = File.ReadAllBytes(target.FilePath);
        Assert.Throws<InvalidDataException>(() => target.PrepareRestore(source.CreateBackup(Password), Password));
        Assert.Equal(before, File.ReadAllBytes(target.FilePath));
    }

    [Fact]
    public void FailedFinalBackupSave_PreservesOldFileAndCleansTemporaryFile()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, "writefailure");
        store.Create("档案");
        var backup = store.CreateBackup(Password);
        var path = workspace.Write("locked.dlauth", "OLD_BACKUP");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => store.SaveBackup(path, backup));
        Assert.Equal("OLD_BACKUP", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories));
    }

    private static void WritePublicFixture(string path, string deviceId, byte[] publicTestKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Profiles = new[] {
            new { ProtocolVersion = 1, DeviceId = deviceId, DisplayName = "公开测试档案", CreatedUtc = DateTimeOffset.UtcNow, Key = publicTestKey }
        } });
        File.WriteAllBytes(path, ProtectedData.Protect(bytes, Encoding.ASCII.GetBytes("DLTOOLS-AUTH-STORE|1"), DataProtectionScope.CurrentUser));
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static AuthorizationProfileStore Store(TestWorkspace workspace, string name) =>
        new(Path.Combine(workspace.Root, name, "profiles.dat"));
}
