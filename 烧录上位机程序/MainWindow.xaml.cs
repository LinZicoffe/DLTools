using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Material.Icons;
using 烧录上位机程序.Services;

namespace 烧录上位机程序;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _forceClose;
    private readonly CloseConfirmationGate _closeConfirmation = new();
    // 已确认退出、正在等待后台任务结束期间为 true：
    // 此时再点 X 直接忽略，避免弹第二个确认框与产生分歧的关闭链
    private bool _closingInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new 烧录上位机程序.ViewModels.MainWindowViewModel();

        // 语言切换入口已随标题栏改版移除：文案固定简体中文，
        // 不再读取历史配置，避免被锁定在无法切回的语言
        I18nService.LoadLanguage("zh_CN");
    }

    public async Task<bool> WaitForCloseDecisionAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allowed = await _closeConfirmation.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowed || _closingInProgress) return false;
            // A second close attempt can enter another native message loop before our continuation runs.
            if (!_closeConfirmation.IsPending) return true;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closingInProgress || _closeConfirmation.IsPending)
        {
            // 等待后台任务退出期间的重复关窗请求：吞掉（首轮流程会完成关闭）
            e.Cancel = true;
            return;
        }

        var host = BackgroundTaskHost.Shared;
        if (!_forceClose && host.IsBusy)
        {
            // 烧录任务跑在线程池上；直接关窗会令进程在原生 DLL 调用中途退出，
            // 芯片可能停在半擦除/半写入状态且 J-Link 会话不会被正常关闭
            e.Cancel = true;
            bool flashing = host.CurrentKind == BackgroundOperationKind.Flashing;
            // A native MessageBox pumps the dispatcher without changing WPF IsEnabled.
            // Publish this gate before showing it so packaging cannot nest another question on top.
            if (!_closeConfirmation.TryBegin()) return;
            MessageBoxResult answer;
            try
            {
                answer = MessageBox.Show(this,
                    flashing
                        ? I18nService.GetString("烧录进行中，强制退出可能使芯片停留在半擦除/半写入状态") + "\n" +
                          I18nService.GetString("确定要停止烧录并退出吗？")
                        : host.CurrentKind == BackgroundOperationKind.Authorization
                            ? "授权档案任务正在进行。确定取消任务，等待安全保存结束后退出吗？"
                            : host.CurrentKind == BackgroundOperationKind.IpScanning
                                ? "IP 扫描正在进行。确定取消扫描，等待在途探测结束后退出吗？"
                                : "项目检查或打包任务正在进行。确定取消任务，等待进程结束及文件恢复后退出吗？",
                    I18nService.GetString("确认退出"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            catch (Exception error)
            {
                _closeConfirmation.Complete(allowFurtherPrompts: true);
                try { LoggerService.Error($"显示退出确认失败: {error.Message}"); } catch { }
                return;
            }
            if (answer != MessageBoxResult.Yes)
            {
                _closeConfirmation.Complete(allowFurtherPrompts: true);
                return;
            }

            _closingInProgress = true;
            IsEnabled = false; // 等待期间禁止从其他页面启动新任务。
            try
            {
                var task = host.CurrentTask;
                var waitingForFlashing = host.CurrentKind == BackgroundOperationKind.Flashing;
                try { host.RequestCancellation(); }
                catch (Exception ex)
                {
                    // A failing cancellation callback must never skip waiting for packaging rollback.
                    try { LoggerService.Error($"请求后台任务取消失败: {ex.Message}"); } catch { }
                }
                finally
                {
                    // Release before awaiting the task: a packaging prompt may itself be waiting on this gate.
                    _closeConfirmation.Complete(allowFurtherPrompts: false);
                }
                if (task is not null)
                {
                    if (waitingForFlashing)
                        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
                    else
                        await task; // 打包必须等进程退出、恢复目录完成，不得超时杀进程。
                }
            }
            catch { /* 任务异常也代表已到终态；烧录保留原来的15秒等待上限。 */ }
            finally
            {
                // 任务可能在确认框显示期间结束，await 此时同步完成。
                // 必须等首次 Closing 返回后再关闭，避免 WPF 拒绝重入 Close。
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _closingInProgress = false;
                    _forceClose = true;
                    if (IsLoaded) Close();
                }));
            }
            return;
        }

        base.OnClosing(e);
    }

    // ===== 自绘标题栏窗口按钮（FluentWindow 剥离了系统标题按钮）=====

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // 最大化/还原时切换按钮图标（初始化早期 MaxButtonIcon 尚未赋值）
        if (MaxButtonIcon is not null)
            MaxButtonIcon.Kind = WindowState == WindowState.Maximized
                ? MaterialIconKind.WindowRestore
                : MaterialIconKind.WindowMaximize;
    }

    private void HeaderRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { /* 按下后原地释放：DragMove 无拖动时抛出，忽略 */ }
    }
}
