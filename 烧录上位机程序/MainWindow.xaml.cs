using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Material.Icons;
using 烧录上位机程序.Pages;
using 烧录上位机程序.Services;

namespace 烧录上位机程序;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _forceClose;
    // 已确认退出、正在等待后台烧录任务结束期间为 true：
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closingInProgress)
        {
            // 等待后台任务退出期间的重复关窗请求：吞掉（首轮流程会完成关闭）
            e.Cancel = true;
            return;
        }

        if (!_forceClose &&
            DataContext is ViewModels.MainWindowViewModel vm &&
            vm.CurrentPage is FlashPage page &&
            page.IsFlashBusy)
        {
            // 烧录任务跑在线程池上；直接关窗会令进程在原生 DLL 调用中途退出，
            // 芯片可能停在半擦除/半写入状态且 J-Link 会话不会被正常关闭
            e.Cancel = true;
            var answer = MessageBox.Show(this,
                I18nService.GetString("烧录进行中，强制退出可能使芯片停留在半擦除/半写入状态") + "\n" +
                I18nService.GetString("确定要停止烧录并退出吗？"),
                I18nService.GetString("确认退出"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            _closingInProgress = true;
            try
            {
                page.RequestStopFlash();
                if (page.FlashTask is Task t)
                    await Task.WhenAny(t, Task.Delay(System.TimeSpan.FromSeconds(15)));
            }
            catch { /* 关窗路径上不再抛出，超时后仍强制退出 */ }
            finally
            {
                _closingInProgress = false;
                _forceClose = true;
                Close();
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
