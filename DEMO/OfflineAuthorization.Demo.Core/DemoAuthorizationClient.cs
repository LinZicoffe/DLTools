using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 烧录上位机程序.Services.Authorization;

namespace OfflineAuthorization.Demo.Core;

public sealed record DemoSnapshot(string Challenge, bool IsActivated, int Failures,
    DateTimeOffset? BlockedUntilUtc, bool ClockRollback, string StatePath)
{
    public bool CanActivate => !IsActivated && !ClockRollback && BlockedUntilUtc is null;
    public string Status => ClockRollback ? "检测到系统时间倒退，请校正时间后刷新状态。"
        : IsActivated ? "已激活：可以使用演示功能，重启后保持激活。"
        : BlockedUntilUtc is { } until ? $"已输错 5 次，暂停尝试至 {until.ToLocalTime():yyyy-MM-dd HH:mm:ss}。"
        : $"等待授权；已失败 {Failures}/5 次。关闭或重启后保留本次申请。";
}

public sealed record DemoActivationResult(bool Accepted, string Message, DemoSnapshot Snapshot);

/// <summary>Demo-only local state machine; the public key and explicit reset are not production security.</summary>
public sealed class DemoAuthorizationClient
{
    private const int HistoryBytes = 1_000_000 / 8;
    private const int MaxFileBytes = 256 * 1024;
    private readonly string _clientName;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<int> _nextChallenge;
    private readonly Action<string, byte[]> _save;
    public string StatePath { get; }

    public DemoAuthorizationClient(string clientName, string? statePath = null,
        Func<DateTimeOffset>? clock = null, Func<int>? nextChallenge = null, Action<string, byte[]>? save = null)
    {
        if (clientName is not ("WinForms" or "WPF")) throw new ArgumentException("未知的 DEMO 类型。", nameof(clientName));
        _clientName = clientName;
        StatePath = Path.GetFullPath(statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLTools", "OfflineAuthorizationDemo", clientName, "state.dat"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nextChallenge = nextChallenge ?? (() => RandomNumberGenerator.GetInt32(1_000_000));
        _save = save ?? ((path, bytes) => AtomicFile.Write(path, bytes));
    }

    public DemoSnapshot Load() => WithState((state, now) => Snapshot(state, now));

    public DemoSnapshot RenewChallenge() => WithState((state, now) =>
    {
        EnsureAvailable(state, now);
        AllocateChallenge(state);
        return Snapshot(state, now);
    });

    public DemoActivationResult Activate(string response) => WithState((state, now) =>
    {
        EnsureAvailable(state, now);
        var key = DemoProfile.CreatePublicKey();
        bool accepted;
        try { accepted = OfflineAuthorizationService.Verify(key, DemoProfile.DeviceId, state.Challenge, response); }
        finally { CryptographicOperations.ZeroMemory(key); }
        if (accepted)
        {
            state.IsActivated = true;
            state.Failures = 0;
            state.BlockedUntilUtc = null;
        }
        else
        {
            state.Failures++;
            if (state.Failures == 5) state.BlockedUntilUtc = now.AddMinutes(30);
        }
        return new DemoActivationResult(accepted, accepted ? "授权成功，已保存激活状态。"
            : state.BlockedUntilUtc is null ? "授权码错误或不是六位数字，请核对设备档案与申请码。"
            : "连续输错 5 次，暂停授权 30 分钟；重启、刷新申请均不会清除次数。", Snapshot(state, now));
    });

    /// <summary>Explicit TEST reset only. Preserve the previous file; never touch the tool's profile store.</summary>
    public DemoSnapshot ResetForTesting()
    {
        using var held = AcquireLock();
        var state = new State { ClientName = _clientName, LastSeenUtc = _clock() };
        AllocateChallenge(state);
        if (File.Exists(StatePath))
            File.Copy(StatePath, StatePath + ".reset-" + Guid.NewGuid().ToString("N"), overwrite: false);
        Save(state);
        return Snapshot(state, state.LastSeenUtc);
    }

    private T WithState<T>(Func<State, DateTimeOffset, T> operation)
    {
        using var held = AcquireLock();
        var state = Read();
        var now = _clock();
        // Do not overwrite the last trustworthy time if the wall clock moved backwards.
        if (now >= state.LastSeenUtc)
        {
            state.LastSeenUtc = now;
            if (state.BlockedUntilUtc is { } until && now >= until)
            {
                state.BlockedUntilUtc = null;
                state.Failures = 0;
            }
        }
        var result = operation(state, now);
        Save(state); // Commit before returning an allocation, failure or successful activation to the UI.
        return result;
    }

    private State Read()
    {
        if (!File.Exists(StatePath))
        {
            var fresh = new State { ClientName = _clientName, LastSeenUtc = _clock() };
            AllocateChallenge(fresh);
            return fresh;
        }
        byte[]? clear = null;
        try
        {
            using var file = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxFileBytes) throw new InvalidDataException("测试状态文件过大。");
            var encrypted = new byte[checked((int)file.Length)]; file.ReadExactly(encrypted);
            clear = ProtectedData.Unprotect(encrypted, Entropy(), DataProtectionScope.CurrentUser);
            var state = JsonSerializer.Deserialize<State>(clear, new JsonSerializerOptions { MaxDepth = 8 });
            if (state is null || state.SchemaVersion != 1 || state.ClientName != _clientName || state.DeviceId != DemoProfile.DeviceId
                || state.UsedChallenges is null || state.UsedChallenges.Length != HistoryBytes || state.LastSeenUtc == default
                || state.Failures is < 0 or > 5 || (state.Failures == 5) != (state.BlockedUntilUtc is not null)
                || (state.IsActivated && state.Failures != 0)
                || state.Challenge is null || state.Challenge.Length != 6 || state.Challenge.Any(c => c is < '0' or > '9'))
                throw new InvalidDataException("测试状态内容无效。");
            var number = int.Parse(state.Challenge, CultureInfo.InvariantCulture);
            if ((state.UsedChallenges[number / 8] & (1 << (number % 8))) == 0) throw new InvalidDataException("申请历史无效。");
            return state;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidDataException)
        {
            throw new InvalidDataException("测试状态损坏或不属于当前 Windows 用户，原文件已保留。可使用“重置测试授权”重新演示。", error);
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    private void Save(State state)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(state);
        try { _save(StatePath, ProtectedData.Protect(clear, Entropy(), DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private byte[] Entropy() => Encoding.ASCII.GetBytes("DLTOOLS-DEMO-STATE|1|" + _clientName);
    private DemoSnapshot Snapshot(State state, DateTimeOffset now) => new(state.Challenge, state.IsActivated,
        state.Failures, state.BlockedUntilUtc, now < state.LastSeenUtc, StatePath);

    private static void EnsureAvailable(State state, DateTimeOffset now)
    {
        if (now < state.LastSeenUtc) throw new InvalidOperationException("检测到系统时间倒退，请校正时间后刷新状态。");
        if (state.IsActivated) throw new InvalidOperationException("已经激活；重新演示请先重置测试授权。");
        if (state.BlockedUntilUtc is not null) throw new InvalidOperationException("当前处于 30 分钟冷却期，请稍后刷新状态。");
    }

    private void AllocateChallenge(State state)
    {
        var start = _nextChallenge();
        if (start is < 0 or >= 1_000_000) throw new InvalidOperationException("随机申请码越界。");
        for (var i = 0; i < 1_000_000; i++)
        {
            var number = (start + i) % 1_000_000;
            var mask = (byte)(1 << (number % 8));
            if ((state.UsedChallenges[number / 8] & mask) != 0) continue;
            state.UsedChallenges[number / 8] |= mask;
            state.Challenge = number.ToString("D6", CultureInfo.InvariantCulture);
            return;
        }
        throw new InvalidOperationException("申请码已耗尽，需要重新配对；不能重复使用历史申请码。");
    }

    private IDisposable AcquireLock()
    {
        var name = "Local\\DLToolsDemo_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StatePath.ToUpperInvariant())));
        var mutex = new Mutex(false, name);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一个测试窗口正在保存状态，请稍后重试。");
            return new HeldMutex(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class HeldMutex(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    private sealed class State
    {
        public int SchemaVersion { get; set; } = 1;
        public string ClientName { get; set; } = "";
        public string DeviceId { get; set; } = DemoProfile.DeviceId;
        public string Challenge { get; set; } = "";
        public byte[] UsedChallenges { get; set; } = new byte[HistoryBytes];
        public bool IsActivated { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset? BlockedUntilUtc { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
    }
}
