using OfflineAuthorization.Demo.Core;
using Xunit;
using 烧录上位机程序.Services.Authorization;

namespace OfflineAuthorization.Demo.Tests;

public sealed class DemoAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DLToolsDemo.Tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private string PathFor(string file = "state.dat") => Path.Combine(_root, file);
    private DemoAuthorizationClient Client(string file = "state.dat", Func<int>? random = null, Action<string, byte[]>? save = null) =>
        new("WinForms", PathFor(file), () => _now, random ?? (() => 0), save);

    [Fact]
    public void Challenge_PreservesLeadingZerosAndSurvivesRestart()
    {
        var snapshot = Client().Load();
        Assert.Equal("000000", snapshot.Challenge);
        Assert.Equal(snapshot, Client(random: () => 999999).Load());
        Assert.True(File.Exists(PathFor()));
    }

    [Theory]
    [InlineData(0, "200533")]
    [InlineData(583194, "677046")]
    [InlineData(999999, "077174")]
    [InlineData(6, "011864")]
    public void Activate_AcceptsProtocolVectorsAndPersists(int challenge, string response)
    {
        var client = Client(random: () => challenge);
        var result = client.Activate(response);
        Assert.True(result.Accepted);
        Assert.True(Client().Load().IsActivated);
        Assert.Throws<InvalidOperationException>(() => Client().Activate(response));
    }

    [Fact]
    public void Renew_InvalidatesOldChallengeWithoutResettingFailures()
    {
        var calls = 0;
        var client = Client(random: () => calls++ == 0 ? 0 : 583194);
        client.Load();
        Assert.False(client.Activate("wrong").Accepted);
        Assert.Equal("583194", client.RenewChallenge().Challenge);
        Assert.Equal(1, Client().Load().Failures);
        Assert.False(client.Activate("200533").Accepted);
        Assert.True(client.Activate("677046").Accepted);
    }

    [Fact]
    public void Renew_NeverReusesIssuedCodesEvenAfterRestart()
    {
        Assert.Equal("000000", Client().Load().Challenge);
        Assert.Equal("000001", Client().RenewChallenge().Challenge);
        Assert.Equal("000002", Client().RenewChallenge().Challenge);
    }

    [Theory]
    [InlineData("")]
    [InlineData("１２３４５６")]
    [InlineData("12345")]
    [InlineData("200534")]
    public void InvalidResponse_CountsAsFailure(string response)
    {
        var result = Client().Activate(response);
        Assert.False(result.Accepted);
        Assert.Equal(1, Client().Load().Failures);
    }

    [Fact]
    public void FiveFailures_BlockForThirtyMinutesAcrossRestartAndRefresh()
    {
        for (var i = 0; i < 5; i++) Assert.False(Client().Activate("bad").Accepted);
        var blocked = Client().Load();
        Assert.Equal(_now.AddMinutes(30), blocked.BlockedUntilUtc);
        Assert.False(blocked.CanActivate);
        Assert.Throws<InvalidOperationException>(() => Client().RenewChallenge());
        Assert.Throws<InvalidOperationException>(() => Client().Activate("200533"));
        _now = _now.AddMinutes(29);
        Assert.False(Client().Load().CanActivate);
        _now = _now.AddMinutes(1);
        Assert.Equal(0, Client().Load().Failures);
        Assert.True(Client().Activate("200533").Accepted);
    }

    [Fact]
    public void ClockRollback_BlocksActivationUntilTimeRecovers()
    {
        Client().Load();
        _now = _now.AddMinutes(-5);
        Assert.True(Client().Load().ClockRollback);
        Assert.Throws<InvalidOperationException>(() => Client().Activate("200533"));
        _now = _now.AddMinutes(5);
        Assert.True(Client().Load().CanActivate);
    }

    [Fact]
    public void CorruptState_IsPreservedUntilExplicitDemoReset()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathFor(), "corrupt");
        Assert.Throws<InvalidDataException>(() => Client().Load());
        Assert.Equal("corrupt", File.ReadAllText(PathFor()));
        var fresh = Client().ResetForTesting();
        Assert.False(fresh.IsActivated);
        Assert.Equal("corrupt", File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.reset-*"))));
    }

    [Fact]
    public void FailedSave_DoesNotReportActivationOrLosePreviousState()
    {
        Client().Load();
        var before = File.ReadAllBytes(PathFor());
        var failing = Client(save: (_, _) => throw new IOException("simulated disk failure"));
        Assert.Throws<IOException>(() => failing.Activate("200533"));
        Assert.Equal(before, File.ReadAllBytes(PathFor()));
        Assert.False(Client().Load().IsActivated);
    }

    [Fact]
    public async Task ConcurrentClients_ShareOnePendingRequestAndKeepFailureCount()
    {
        var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Client().Load().Challenge)));
        Assert.Single(requests.Distinct());
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Client().Activate("bad"))));
        Assert.Equal(4, Client().Load().Failures);
    }

    [Fact]
    public void WinFormsAndWpf_HaveIndependentActivationState()
    {
        Client().Activate("200533");
        var wpf = new DemoAuthorizationClient("WPF", PathFor("wpf.dat"), () => _now, () => 0);
        Assert.False(wpf.Load().IsActivated);
        Assert.True(Client().Load().IsActivated);
        Assert.True(wpf.Activate("200533").Accepted);
    }

    [Fact]
    public async Task ExportedFixture_ImportsIntoActualGeneratorStoreAndAuthorizesClient()
    {
        var backup = PathFor("demo.dlauth");
        await DemoProfile.ExportAsync(backup);
        var generator = new AuthorizationProfileStore(PathFor("generator.dat"));
        var original = generator.Create("unrelated test customer");
        using var plan = generator.PrepareRestore(File.ReadAllBytes(backup), DemoProfile.BackupPassword);
        Assert.Equal(1, plan.NewProfiles);
        generator.CommitRestore(plan);
        Assert.Contains(generator.Load(), p => p.DeviceId == original.DeviceId);
        var snapshot = Client(random: () => 6).Load();
        var response = generator.Generate(DemoProfile.DeviceId, snapshot.Challenge);
        Assert.Equal("011864", response);
        Assert.True(Client().Activate(response).Accepted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
