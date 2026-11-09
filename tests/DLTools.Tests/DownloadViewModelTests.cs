using System.IO;
using System.Windows.Threading;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;
using 烧录上位机程序.ViewModels;

namespace DLTools.Tests;

public class DownloadViewModelTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private static byte[] TestData(int size, int seed = 11)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private DownloadScheduler CreateScheduler()
    {
        var server = new FakeSegmentServer(TestData(512 * 1024)); // 单流路径，测试更快
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        return new DownloadScheduler(() => new DownloadEngine(transport,
            retryDelay: (_, _) => Task.CompletedTask), maxConcurrentTasks: 2);
    }

    [Fact]
    public Task AddDownload_InvalidUrl_ShowsHintAndKeepsQueueEmpty() => OnStaAsync(() =>
    {
        var scheduler = CreateScheduler();
        var vm = new DownloadViewModel(scheduler, Path.Combine(_workspace.Root, "settings.json"))
        {
            DownloadUrl = "file://C:/fw.bin",
            DestinationDirectory = Path.Combine(_workspace.Root, "out")
        };

        vm.AddDownloadCommand.Execute(null);
        Assert.Contains("http/https", vm.StatusText);
        Assert.Empty(scheduler.Tasks);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AddDownload_InvalidSha256_ShowsHintWithoutEnqueue() => OnStaAsync(() =>
    {
        var scheduler = CreateScheduler();
        var vm = new DownloadViewModel(scheduler, Path.Combine(_workspace.Root, "settings.json"))
        {
            DownloadUrl = "http://8.8.8.8/fw.bin",
            DestinationDirectory = Path.Combine(_workspace.Root, "out"),
            Sha256Text = "zz"
        };

        vm.AddDownloadCommand.Execute(null);
        Assert.Contains("SHA-256", vm.StatusText);
        Assert.Empty(scheduler.Tasks);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AddDownload_CreatesMissingDirectory_QueuesTaskAndClearsUrl() => OnStaAsync(() =>
    {
        var scheduler = CreateScheduler();
        var destination = Path.Combine(_workspace.Root, "brand-new", "dir");
        var vm = new DownloadViewModel(scheduler, Path.Combine(_workspace.Root, "settings.json"))
        {
            DownloadUrl = "http://8.8.8.8/fw.bin",
            DestinationDirectory = destination
        };

        vm.AddDownloadCommand.Execute(null);
        Assert.True(Directory.Exists(destination));
        var task = Assert.Single(scheduler.Tasks);
        Assert.Equal("http://8.8.8.8/fw.bin", task.Options.Url);
        Assert.Equal(Path.GetFullPath(destination), task.Options.DestinationDirectory);
        Assert.Empty(vm.DownloadUrl);
        Assert.Contains("队列", vm.StatusText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SchedulerEvents_CreateAndRefreshRowsOnUiThread() => OnStaAsync(async () =>
    {
        var scheduler = CreateScheduler();
        var vm = new DownloadViewModel(scheduler, Path.Combine(_workspace.Root, "settings.json"))
        {
            DownloadUrl = "http://8.8.8.8/fw.bin",
            DestinationDirectory = Path.Combine(_workspace.Root, "out")
        };
        Directory.CreateDirectory(vm.DestinationDirectory);

        vm.AddDownloadCommand.Execute(null);
        await WaitForAsync(() => vm.Tasks.Count == 1, "任务行应出现在列表");
        var row = vm.Tasks[0];

        await WaitForAsync(() => row.State == DownloadTaskState.Completed, "下载应完成");
        Assert.Equal(100, row.ProgressPercent);
        Assert.Equal("已完成", row.StateText);
        Assert.Equal("fw.bin", row.DisplayName); // 完成后显示文件名
        Assert.Contains("fw.bin", row.Message);
    });

    [Fact]
    public Task SaveSettings_PersistsStoreAndAppliesConcurrencyToScheduler() => OnStaAsync(() =>
    {
        var scheduler = CreateScheduler();
        var settingsPath = Path.Combine(_workspace.Root, "settings.json");
        var vm = new DownloadViewModel(scheduler, settingsPath)
        {
            DefaultConnections = 6,
            MaxConcurrentTasks = 2,
            MaxRateKB = 2048,
            ProxyHost = "proxy.example",
            ProxyPort = 8080
        };

        vm.SaveSettingsCommand.Execute(null);
        Assert.Contains("已保存", vm.StatusText);
        var reloaded = new DownloadSettingsStore(settingsPath).Load();
        Assert.Equal(6, reloaded.DefaultConnections);
        Assert.Equal(2, reloaded.MaxConcurrentTasks);
        Assert.Equal(2048, reloaded.MaxRateKB);
        Assert.Equal("proxy.example", reloaded.ProxyHost);
        Assert.Equal(8080, reloaded.ProxyPort);
        Assert.Equal(2, scheduler.MaxConcurrentTasks);
        return Task.CompletedTask;
    });

    [Fact]
    public Task LoadManifest_ThenImportSelected_EnqueuesWithForcedSha256AndCompletes() => OnStaAsync(async () =>
    {
        var data = TestData(512 * 1024);
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        var json = "{\"schemaVersion\":1,\"files\":[{\"name\":\"vmfw.bin\",\"size\":" + data.Length
            + ",\"sha256\":\"" + sha256 + "\",\"urls\":[\"https://8.8.8.8/vmfw.bin\"]}]}";
        var manifestService = new MirrorManifestService(new FakeDownloadTransport(ServeManifest));
        var scheduler = CreateScheduler();
        var vm = new DownloadViewModel(scheduler, Path.Combine(_workspace.Root, "settings.json"),
            manifestService: manifestService)
        {
            ManifestUrl = "https://8.8.8.8/manifest.json",
            DestinationDirectory = Path.Combine(_workspace.Root, "out")
        };
        Directory.CreateDirectory(vm.DestinationDirectory);

        await vm.LoadManifestCommand.ExecuteAsync(null);
        Assert.Single(vm.ManifestFiles);
        Assert.True(vm.ManifestLoaded);
        Assert.Equal(1, vm.SelectedFileCount);

        await vm.ImportSelectedCommand.ExecuteAsync(null);
        var task = Assert.Single(scheduler.Tasks);
        Assert.Equal("https://8.8.8.8/vmfw.bin", task.Options.Url);
        Assert.Equal(sha256, task.Options.ExpectedSha256); // 清单分发强制校验
        Assert.Contains("已导入", vm.StatusText);

        await WaitForAsync(() => task.State == DownloadTaskState.Completed, "清单任务应下载完成");
        Assert.True(File.Exists(Path.Combine(vm.DestinationDirectory, "vmfw.bin")));

        return;

        Task<TransportResponse> ServeManifest(TransportRequest request, CancellationToken _)
        {
            var response = request.Url.Contains("manifest")
                ? FakeDownloadTransport.OkFull(json.Length, System.Text.Encoding.UTF8.GetBytes(json))
                : FakeDownloadTransport.Partial(data.Length, 0, 0, [data[0]]);
            return Task.FromResult(response);
        }
    });

    private static async Task WaitForAsync(Func<bool> condition, string hint)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("等待超时：" + hint);
            // 让 Dispatcher 队列里的 InvokeAsync 回调（行刷新）先执行
            await Task.Delay(15);
        }
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public void Dispose() => _workspace.Dispose();
}
