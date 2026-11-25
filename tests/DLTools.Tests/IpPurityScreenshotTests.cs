using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Pages;
using 烧录上位机程序.Services.IpPurity;
using 烧录上位机程序.ViewModels;

namespace DLTools.Tests;

public class IpPurityScreenshotTests
{
    [Fact]
    public Task Capture_WhenRequested_WritesWindowShots()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DLTOOLS_IPPURITY_SHOTS"), "1", StringComparison.Ordinal))
            return Task.CompletedTask;
        return OnStaAsync(CaptureAsync);
    }

    private static async Task CaptureAsync()
    {
        var app = new 烧录上位机程序.App();
        app.InitializeComponent();
        var window = new 烧录上位机程序.MainWindow { Left = 40, Top = 40 };
        window.Show();
        var viewModel = (MainWindowViewModel)window.DataContext;
        viewModel.ShowIpPurityCommand.Execute(null);
        var page = (IpPurityPage)viewModel.CurrentPage!;
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "images"));
        Directory.CreateDirectory(directory);

        await LayoutAsync(window);
        await ShootAsync(window, 1280, 800, Path.Combine(directory, "ip-purity-idle-1280.png"));
        await ShootAsync(window, 1000, 700, Path.Combine(directory, "ip-purity-idle-1000.png"));

        page.ViewModel.ShowSnapshot(Running());
        await LayoutAsync(window);
        await ShootAsync(window, 1280, 800, Path.Combine(directory, "ip-purity-running-1280.png"));
        await ShootAsync(window, 1000, 700, Path.Combine(directory, "ip-purity-running-1000.png"));

        page.ViewModel.ShowSnapshot(Done());
        await LayoutAsync(window);
        await ShootAsync(window, 1280, 800, Path.Combine(directory, "ip-purity-done-1280.png"));
        await ShootAsync(window, 1000, 700, Path.Combine(directory, "ip-purity-done-1000.png"));

        page.ViewModel.HeaderStatus = "检测已取消";
        page.ViewModel.Summary = "已取消。可以重新检测。";
        page.ViewModel.ShowIdentity = false;
        page.ViewModel.ShowDetails = false;
        page.ViewModel.ShowFailure = false;
        page.ViewModel.ShowAi = false;
        page.ViewModel.ShowNotes = true;
        page.ViewModel.RingPrimary = "—";
        page.ViewModel.RingSecondary = "尚无评分";
        page.ViewModel.RingTone = "none";
        page.ViewModel.RingValue = 0;
        await LayoutAsync(window);
        await ShootAsync(window, 1280, 800, Path.Combine(directory, "ip-purity-cancel-1280.png"));
        await ShootAsync(window, 1000, 700, Path.Combine(directory, "ip-purity-cancel-1000.png"));
        window.Close();
    }

    private static async Task ShootAsync(Window window, double width, double height, string path)
    {
        window.Width = width;
        window.Height = height;
        await LayoutAsync(window);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Task LayoutAsync(Window window) =>
        window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle).Task;

    private static IpPuritySnapshot Running() => new(
        IpPurityPhase.Running, 2, "正在检测 ChatGPT",
        new IpPurityExit("203.0.113.7", "Cloudflare", 54, "US", "SJC", null, null, null, null, null, []),
        true,
        new IpPurityIntel("203.0.113.7", "IPinfo", "high", "AS16509", "Amazon.com, Inc.", "Amazon Technologies Inc.", null, null, null,
            "hosting", new IpPurityFlags(true, false, true, false, false, false),
            new IpPurityGeo("US", "美国", "Virginia", "Ashburn"), []),
        true, [], [], false, null, false, null, false, null, false, TimeSpan.FromSeconds(2));

    private static IpPuritySnapshot Done()
    {
        var intel = new IpPurityIntel("203.0.113.7", "IPinfo", "high", "AS16509", "Amazon.com, Inc.", "Amazon Technologies Inc.", null, null, null,
            "hosting", new IpPurityFlags(true, false, true, false, false, false),
            new IpPurityGeo("US", "美国", "Virginia", "Ashburn"), []);
        var score = IpPurityScoring.ComputeV2(new IpPurityScoreRequest(
            "203.0.113.7", IpPurityParse.ToScoreIntel(intel),
            [new("ChatGPT", "risk", true), new("Claude", "region", true)],
            new IpPurityScoreConsistency(true, 2, 0),
            new IpPurityScoreLeak("unknown", [], true, true)));
        var chatgpt = new IpPurityServiceVerdict("risk", "可连接但有风控风险", true, "所在地区在支持列表内", ["机房 IP", "代理"], ["命中风险标记：机房 IP、代理"],
            true, "203.0.113.7", "US", "可达 · 57 ms", true, "可达 · 56 ms", 200);
        var claude = new IpPurityServiceVerdict("region", "地区受限", true, "所在地区在支持列表内", [], ["Anthropic 的接口返回了地区限制（HTTP 403），当前出口所在地区不受支持"],
            true, "198.51.100.9", "US", "可达 · 56 ms", false, "已拒绝 · HTTP 403", 403);
        var splitIntel = new IpPurityIntel("198.51.100.9", "IPinfo", "high", "AS45102", "Alibaba", "Alibaba", null, null, null,
            "hosting", new IpPurityFlags(true, false, false, false, true, false), new IpPurityGeo("US", null, null, null), []);
        return new IpPuritySnapshot(
            IpPurityPhase.Done, 6, "检测完成",
            new IpPurityExit("203.0.113.7", "Cloudflare", 54, "US", "SJC", null, null, null, null, null, []),
            true, intel, true,
            [
                new IpPurityServiceOutcome("chatgpt", "ChatGPT", "https://chatgpt.com/", chatgpt, null, null),
                new IpPurityServiceOutcome("claude", "Claude", "https://claude.ai/", claude, splitIntel, IpPurityScoring.PurityOf("198.51.100.9", splitIntel))
            ],
            [
                new IpPurityUdpObservation("stun.cloudflare.com", 3478, true, "203.0.113.7", 80, null),
                new IpPurityUdpObservation("stun.l.google.com", 19302, true, null, null, "请求超时"),
                new IpPurityUdpObservation("stun.miwifi.com", 3478, false, "192.0.2.44", 20, null),
                new IpPurityUdpObservation("stun.qq.com", 3478, false, null, null, "请求超时")
            ],
            true, null, true,
            new IpPurityExitSummary(
                [
                    new IpPurityExitRow("Cloudflare", "203.0.113.7", false, "match", true),
                    new IpPurityExitRow("ChatGPT", "203.0.113.7", false, "match", true),
                    new IpPurityExitRow("Claude", "198.51.100.9", false, "diff", false)
                ],
                "203.0.113.7", null, 2, 0, true),
            true, score, false, TimeSpan.FromSeconds(8.4));
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
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
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
