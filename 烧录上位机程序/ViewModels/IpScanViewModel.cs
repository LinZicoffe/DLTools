using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Services;

namespace 烧录上位机程序.ViewModels;

public sealed record IpScanDevice(string Ip, long RoundtripTimeMs)
{
    public string Status => "可达";
}

public partial class IpScanViewModel : ObservableObject
{
    private readonly IIpScanService _service;
    private readonly BackgroundTaskHost _host;
    private readonly Dispatcher _dispatcher;
    private Task? _ownedTask;
    private bool _cancelRequested;

    public IpScanViewModel(IIpScanService? service = null, BackgroundTaskHost? host = null, Dispatcher? dispatcher = null)
    {
        _service = service ?? new IpScanService();
        _host = host ?? BackgroundTaskHost.Shared;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public ObservableCollection<IpScanDevice> Devices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputHint))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _inputIp = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _completed;

    [ObservableProperty]
    private string _status = "输入本机同网段的任一 IPv4 地址，然后开始扫描。";

    public bool IsIdle => !IsBusy;
    public string ProgressText => $"{Completed}/{IpScanService.HostCount}";
    public string FoundText => $"已发现 {Devices.Count} 台设备";
    public string InputHint => IpScanService.TryParseInput(InputIp, out var address, out var error)
        ? $"扫描范围：{string.Join(".", address.GetAddressBytes().Take(3))}.1 ～ .254（/24，跳过 .0 和 .255）"
        : error;

    private bool CanScan() => !IsBusy && IpScanService.TryParseInput(InputIp, out _, out _);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!CanScan()) return;
        var input = InputIp;
        IsBusy = true;
        _cancelRequested = false;
        try
        {
            // Only clear the previous results if this page actually acquires global operation ownership.
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_host.TryStart(BackgroundOperationKind.IpScanning, async token =>
            {
                await ready.Task.ConfigureAwait(false);
                await _service.ScanAsync(input, update => _dispatcher.InvokeAsync(() =>
                {
                    if (_cancelRequested || token.IsCancellationRequested) return;
                    Completed = update.Completed;
                    if (update.Result.Status == IpProbeStatus.Reachable)
                    {
                        Devices.Add(new(update.Address.ToString(), update.Result.RoundtripTimeMs ?? 0));
                        OnPropertyChanged(nameof(FoundText));
                    }
                }).Task, token).ConfigureAwait(false);
            }, out var task))
            {
                Status = "另一个后台任务正在进行，请等待结束后重试。";
                return;
            }
            _ownedTask = task;
            try
            {
                Devices.Clear();
                OnPropertyChanged(nameof(FoundText));
                Completed = 0;
                Status = "正在扫描…（最多并发 32 个，每个地址等待 500ms）";
                CancelCommand.NotifyCanExecuteChanged();
            }
            finally { ready.TrySetResult(); }
            await task;
            Status = _cancelRequested ? "扫描已取消，保留已发现的设备。"
                : Devices.Count == 0 ? "未发现设备（部分设备会禁用 ICMP 回复）"
                : $"扫描完成，发现 {Devices.Count} 台设备。";
        }
        catch (OperationCanceledException) { Status = "扫描已取消，保留已发现的设备。"; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException
            or System.Net.NetworkInformation.NetworkInformationException or System.Net.Sockets.SocketException
            or System.Security.SecurityException or NotSupportedException)
        {
            Status = "扫描失败：" + error.Message;
        }
        finally
        {
            _ownedTask = null;
            IsBusy = false;
        }
    }

    private bool CanCancel() => IsBusy && !_cancelRequested && _ownedTask is { IsCompleted: false }
        && ReferenceEquals(_host.CurrentTask, _ownedTask);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanCancel()) return;
        _cancelRequested = true;
        Status = "正在取消，等待在途探测结束…";
        CancelCommand.NotifyCanExecuteChanged();
        _host.RequestCancellation();
    }
}
