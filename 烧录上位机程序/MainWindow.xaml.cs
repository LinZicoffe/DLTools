using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using 烧录上位机程序.Pages;
using 烧录上位机程序.Services;

namespace 烧录上位机程序;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _forceClose;
    // 已确认退出、正在等待后台烧录任务结束期间为 true：
    // 此时再点 X 直接忽略，避免弹第二个确认框与产生分歧的关闭链
    private bool _closingInProgress;
    // 语言下拉框程序化赋值期间为 true：此时 SelectionChanged 属初始化回声，
    // 不应再触发 LoadLanguage/写配置（否则每次启动都重写 user_prefs.json）
    private bool _suppressLanguageEvent;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new 烧录上位机程序.ViewModels.MainWindowViewModel();

        // 设置默认语言
        var vm = (烧录上位机程序.ViewModels.MainWindowViewModel)DataContext!;
        var lang = ConfigService.Get<string>("language") ?? "zh_CN";
        I18nService.LoadLanguage(lang);
        UpdateLanguageCombo(lang);
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

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvent) return;
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
        {
            var lang = item.Tag?.ToString() ?? "zh_CN";
            I18nService.LoadLanguage(lang);
            ConfigService.Set("language", lang);
        }
    }

    private void UpdateLanguageCombo(string lang)
    {
        if (LanguageCombo is ComboBox combo)
        {
            _suppressLanguageEvent = true;
            try
            {
                foreach (ComboBoxItem item in combo.Items)
                {
                    if (item.Tag?.ToString() == lang)
                    {
                        item.IsSelected = true;
                        break;
                    }
                }
            }
            finally
            {
                _suppressLanguageEvent = false;
            }
        }
    }
}
