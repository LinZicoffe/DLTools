using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Material.Icons;
using 烧录上位机程序.Services;
using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _forceClose;
    private readonly CloseConfirmationGate _closeConfirmation = new();
    // 已确认退出、正在等待后台任务结束期间为 true：
    // 此时再点 X 直接忽略，避免弹第二个确认框与产生分歧的关闭链
    private bool _closingInProgress;

    // ===== 左侧导航液态玻璃选中胶囊（样式见 UiStyles.xaml 的 NavCapsule 系列资源）=====
    // 顺序与 NavItemsHost 里的导航按钮一一对应，新增导航时两处需同步
    private readonly Button[] _navButtons;
    private int _navSelectionIndex;
    private double _navCapsuleY = double.NaN; // NaN = 尚未定位过，首次必须无动画落位

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();

        // 语言切换入口已随标题栏改版移除：文案固定简体中文，
        // 不再读取历史配置，避免被锁定在无法切回的语言
        I18nService.LoadLanguage("zh_CN");

        // 胶囊跟随：ViewModel 切页时七个 IsXxxSelected 一起通知，
        // 复用该通知驱动胶囊滑动，不改动 ViewModel
        _navButtons = NavItemsHost.Children.OfType<Button>().ToArray();
        if (DataContext is INotifyPropertyChanged notifier)
            notifier.PropertyChanged += NavSelection_PropertyChanged;
        Loaded += (_, _) => PositionNavCapsule(animate: false); // 启动直接落位，无飞入
        SizeChanged += (_, _) => PositionNavCapsule(animate: false); // 尺寸变化无动画校正，避免漂移
    }

    private void NavSelection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel viewModel) return;
        // 只有变为 true 的那一项代表本次切换的新选中页（其余五项为 false）
        var index = e.PropertyName switch
        {
            nameof(MainWindowViewModel.IsFlashSelected) when viewModel.IsFlashSelected => 0,
            nameof(MainWindowViewModel.IsPackagingSelected) when viewModel.IsPackagingSelected => 1,
            nameof(MainWindowViewModel.IsAuthorizationSelected) when viewModel.IsAuthorizationSelected => 2,
            nameof(MainWindowViewModel.IsIpScanSelected) when viewModel.IsIpScanSelected => 3,
            nameof(MainWindowViewModel.IsIpPuritySelected) when viewModel.IsIpPuritySelected => 4,
            nameof(MainWindowViewModel.IsDownloadSelected) when viewModel.IsDownloadSelected => 5,
            nameof(MainWindowViewModel.IsCleanupSelected) when viewModel.IsCleanupSelected => 6,
            _ => -1,
        };
        if (index < 0) return;
        _navSelectionIndex = index;
        if (IsLoaded) PositionNavCapsule(animate: true);
    }

    /// <summary>把胶囊定位到当前选中导航按钮处。animate=false 用于启动落位
    /// 与窗口尺寸校正（先停掉在途动画再写属性）；animate=true 播放滑动与拉伸回弹。</summary>
    private void PositionNavCapsule(bool animate)
    {
        if (_navSelectionIndex < 0 || _navSelectionIndex >= _navButtons.Length) return;
        var button = _navButtons[_navSelectionIndex];
        if (NavCapsuleLayer.ActualWidth <= 0 || button.ActualHeight <= 0) return;

        NavCapsule.Width = NavCapsuleLayer.ActualWidth; // 宽度跟随导航可用宽度
        var targetY = button.TransformToAncestor(NavItemsHost).Transform(new Point(0, 0)).Y;
        NavCapsule.Opacity = 1;
        if (!animate || double.IsNaN(_navCapsuleY))
        {
            NavCapsuleTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NavCapsuleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NavCapsuleTranslate.Y = targetY;
            NavCapsuleScale.ScaleY = 1;
            _navCapsuleY = targetY;
            return;
        }

        // 滑动：460ms BackEase(EaseOut, Amplitude=0.45) 到位回弹
        var slide = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(460))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 },
        };
        NavCapsuleTranslate.BeginAnimation(TranslateTransform.YProperty, slide);

        // 纵向拉伸：移动中拉长 → 微缩 → 复位，模拟液态玻璃的粘滞感
        var stretch = new DoubleAnimationUsingKeyFrames();
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.24, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        });
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(460))));
        NavCapsuleScale.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);
        _navCapsuleY = targetY;
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
        var scheduler = 烧录上位机程序.Services.Downloads.DownloadScheduler.Shared;
        var downloadsBusy = scheduler.HasActiveWork;
        if (!_forceClose && (host.IsBusy || downloadsBusy))
        {
            // 烧录任务跑在线程池上；直接关窗会令进程在原生 DLL 调用中途退出，
            // 芯片可能停在半擦除/半写入状态且 J-Link 会话不会被正常关闭
            e.Cancel = true;
            bool flashing = host.IsBusy && host.CurrentKind == BackgroundOperationKind.Flashing;
            var downloadLine = downloadsBusy ? "\n下载任务将全部暂停，未完成部分保留续传记录。" : "";
            // A native MessageBox pumps the dispatcher without changing WPF IsEnabled.
            // Publish this gate before showing it so packaging cannot nest another question on top.
            if (!_closeConfirmation.TryBegin()) return;
            MessageBoxResult answer;
            try
            {
                var hostQuestion = !host.IsBusy ? ""
                    : flashing
                        ? I18nService.GetString("烧录进行中，强制退出可能使芯片停留在半擦除/半写入状态") + "\n" +
                          I18nService.GetString("确定要停止烧录并退出吗？")
                        : host.CurrentKind == BackgroundOperationKind.Authorization
                            ? "授权档案任务正在进行。确定取消任务，等待安全保存结束后退出吗？"
                            : host.CurrentKind == BackgroundOperationKind.IpScanning
                                ? "IP 扫描正在进行。确定取消扫描，等待在途探测结束后退出吗？"
                                : host.CurrentKind == BackgroundOperationKind.IpPurityCheck
                                    ? "IP 纯净度检测正在进行。确定取消检测，等待在途请求结束后退出吗？"
                                    : host.CurrentKind == BackgroundOperationKind.DiskCleanup
                                    ? "C盘清理正在进行。确定取消清理，等待审计日志写入完成后退出吗？"
                                    : "项目检查或打包任务正在进行。确定取消任务，等待进程结束及文件恢复后退出吗？";
                var downloadQuestion = !downloadsBusy
                    ? ""
                    : host.IsBusy ? "下载任务正在进行，将随上述任务一并暂停（保留续传记录）。确定退出吗？" + downloadLine
                    : "下载任务正在进行。确定暂停全部下载并退出吗？未完成部分将保留续传记录，下次启动后可继续。";
                answer = MessageBox.Show(this,
                    (hostQuestion + downloadQuestion).Trim(),
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
                if (host.CurrentKind == BackgroundOperationKind.IpPurityCheck && DataContext is MainWindowViewModel shell)
                    shell.AbandonIpPurity();
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
                    try
                    {
                        if (waitingForFlashing)
                        {
                            // 烧录超时兜底：原生 J-Link 调用（如 USB 异常）可能不响应取消，
                            // 超时后芯片状态未知——必须留下审计记录并明确告知用户，不允许静默强关。
                            var timeout = Task.Delay(TimeSpan.FromSeconds(15));
                            if (await Task.WhenAny(task, timeout) != timeout) { /* 任务如期退出 */ }
                            else
                            {
                                try { LoggerService.Error("烧录任务 15 秒未退出（原生调用未响应取消），即将强制关闭窗口；芯片状态未知，建议重新上电并重烧验证"); } catch { }
                                MessageBox.Show(this,
                                    I18nService.GetString("烧录未能在 15 秒内停止，程序即将强制退出。芯片可能处于未知状态，请对目标板重新上电并重新烧录验证。"),
                                    I18nService.GetString("警告"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                        else
                        {
                            await task; // 打包必须等进程退出、恢复目录完成，不得超时杀进程。
                        }
                    }
                    catch
                    {
                        // host 任务异常也代表已到终态；下载排空必须继续执行，不能被跳过
                    }
                }
                if (downloadsBusy)
                    await scheduler.CancelAllAndDrainAsync(); // 下载引擎排空后日志已落盘，可安全退出
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
