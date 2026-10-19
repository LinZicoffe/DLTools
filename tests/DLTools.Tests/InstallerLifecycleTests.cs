using Microsoft.Win32;
using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

[Collection("Packaging pipeline")]
public sealed class InstallerLifecycleTests
{
    [InstallerLifecycleFact]
    public async Task GeneratedInstaller_InstallLaunchUpgradeUninstall_PreservesUserDataAndLiteralMetadata()
    {
        var ws = new TestWorkspace();
        var id = Guid.NewGuid();
        var install = Path.Combine(ws.Root, "安装 目录");
        var uninstaller = Path.Combine(install, "unins000.exe");
        var cleanupSucceeded = false;
        var runner = new ExternalProcessRunner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            var project = ProjectFixtures.Sdk(ws, "LifecycleProbe");
            var options = (await PipelineFixture.Options(ws, project)) with
            {
                AppId = id, AppName = "DL 安装验证 \"引号\" {花括号} {#1+1}", AppVersion = "1.0.0",
                InstallerFileName = "生命周期 第一版.exe", SelfContained = false,
                CreateDesktopShortcut = false, LaunchAfterInstall = false
            };
            var service = new PackagingService(stateDirectory: Path.Combine(ws.Root, "state"));
            var first = await service.PackageAsync(options, null, _ => Task.FromResult(true), timeout.Token);
            Assert.True(first.Succeeded, first.Error + "\n" + first.LogPath);
            await InstallAsync(first.InstallerPath!, install, runner, timeout.Token);
            Assert.True(File.Exists(Path.Combine(install, options.Project.MainExecutable)));
            Assert.True(File.Exists(Path.Combine(install, "LifecycleProbe.runtimeconfig.json")));
            Assert.Equal("<runtime>keep me</runtime>", File.ReadAllText(Path.Combine(install, "runtime-data.xml")));
            AssertRegistration(id, options.AppName, "1.0.0", install);
            var marker = Path.Combine(ws.Root, "launched-marker.txt");
            var launch = await runner.RunAsync(Path.Combine(install, options.Project.MainExecutable),
                ["--probe", marker], install, cancellationToken: timeout.Token);
            Assert.Equal(0, launch.ExitCode);
            Assert.Equal("started", File.ReadAllText(marker));

            var userData = Path.Combine(install, "user-data.keep");
            File.WriteAllText(userData, "user-owned runtime data");
            var second = await service.PackageAsync(options with
            {
                AppVersion = "1.0.1", InstallerFileName = "生命周期 第二版 改名.exe"
            }, null, _ => Task.FromResult(true), timeout.Token);
            Assert.True(second.Succeeded, second.Error + "\n" + second.LogPath);
            Assert.NotEqual(first.InstallerPath, second.InstallerPath);
            await InstallAsync(second.InstallerPath!, install, runner, timeout.Token);
            AssertRegistration(id, options.AppName, "1.0.1", install);
            Assert.Equal("user-owned runtime data", File.ReadAllText(userData));
            File.Delete(marker);
            var upgradedLaunch = await runner.RunAsync(Path.Combine(install, options.Project.MainExecutable),
                ["--probe", marker], install, cancellationToken: timeout.Token);
            Assert.Equal(0, upgradedLaunch.ExitCode);
            Assert.Equal("started", File.ReadAllText(marker));
            await UninstallAsync(uninstaller, install, runner, timeout.Token);
            Assert.Empty(FindRegistration(id));
            Assert.False(File.Exists(Path.Combine(install, options.Project.MainExecutable)));
            Assert.Equal("user-owned runtime data", File.ReadAllText(userData));
            cleanupSucceeded = true;
        }
        finally
        {
            // Cleanup is restricted to this test's fresh GUID AppId and owned installation path.
            if (!cleanupSucceeded)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                if (File.Exists(uninstaller))
                {
                    try { await UninstallAsync(uninstaller, install, runner, cleanupTimeout.Token); }
                    catch (Exception error)
                    {
                        throw new IOException($"测试安装清理失败。保留诊断目录：{ws.Root}；AppId：{id:D}", error);
                    }
                }
                if (FindRegistration(id).Count != 0)
                    throw new IOException($"测试卸载项仍存在。保留诊断目录：{ws.Root}；AppId：{id:D}");
                cleanupSucceeded = true;
            }
            if (cleanupSucceeded) ws.Dispose();
        }
    }

    private static async Task InstallAsync(string package, string directory, ExternalProcessRunner runner, CancellationToken token)
    {
        var result = await runner.RunAsync(package,
            ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOICONS", "/DIR=" + directory],
            Path.GetDirectoryName(package)!, cancellationToken: token);
        Assert.True(result.ExitCode == 0, $"安装失败：{result.ExitCode}\n{result.StandardError}");
    }

    private static async Task UninstallAsync(string uninstaller, string directory, ExternalProcessRunner runner, CancellationToken token)
    {
        Assert.True(File.Exists(uninstaller), uninstaller);
        var result = await runner.RunAsync(uninstaller, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
            directory, cancellationToken: token);
        Assert.True(result.ExitCode == 0, $"卸载失败：{result.ExitCode}\n{result.StandardError}");
    }

    private static void AssertRegistration(Guid id, string name, string version, string install)
    {
        var registration = Assert.Single(FindRegistration(id));
        Assert.StartsWith(name, registration.DisplayName);
        Assert.Contains("{花括号} {#1+1}", registration.DisplayName);
        Assert.DoesNotContain("{{", registration.DisplayName);
        Assert.Equal(version, registration.Version);
        Assert.Equal(install.TrimEnd('\\'), registration.Location.TrimEnd('\\'), ignoreCase: true);
    }

    private static List<(string DisplayName, string Version, string Location)> FindRegistration(Guid id)
    {
        var results = new List<(string, string, string)>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var entry = user.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + id.ToString("D") + "}_is1");
            if (entry is null) continue;
            results.Add((entry.GetValue("DisplayName")?.ToString() ?? "", entry.GetValue("DisplayVersion")?.ToString() ?? "",
                entry.GetValue("InstallLocation")?.ToString() ?? ""));
        }
        // HKCU's uninstall view can be shared between WOW64 registry views. Count physical entries once.
        return results.Distinct().ToList();
    }
}

public sealed class InstallerLifecycleFactAttribute : FactAttribute
{
    public InstallerLifecycleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLTOOLS_INTEGRATION") != "1"
            || Environment.GetEnvironmentVariable("DLTOOLS_INSTALL_INTEGRATION") != "1")
            Skip = "同时设置 DLTOOLS_INTEGRATION=1 和 DLTOOLS_INSTALL_INTEGRATION=1，以允许当前用户的临时安装/升级/卸载验证。";
    }
}
