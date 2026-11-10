using System;
using System.Windows;
using 烧录上位机程序.Services;

namespace 烧录上位机程序;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：任何未处理异常先落日志，避免静默崩溃、现场无从排查
        DispatcherUnhandledException += (s, args) =>
        {
            try { LoggerService.Error($"UI 线程未处理异常: {args.Exception}"); } catch { }
            MessageBox.Show($"{I18nService.GetString("发生未处理的错误：")}\n\n{args.Exception.Message}\n\n{I18nService.GetString("详情已写入日志")}",
                I18nService.GetString("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try { LoggerService.Error($"未处理异常(非UI线程): {args.ExceptionObject}"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            try { LoggerService.Error($"未观察的Task异常: {args.Exception}"); } catch { }
            args.SetObserved();
        };

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        ConfigService.Initialize();
        I18nService.LoadLanguage(ConfigService.Get<string>("language") ?? "zh_CN");

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 配置写入有 200ms 防抖，退出前强制落盘避免最后一次修改丢失
        ConfigService.Flush();
        // 正常关窗流程已排空下载任务；这里兜底等待在途引擎退出（续传日志随取消路径落盘）
        try
        {
            Services.Downloads.DownloadScheduler.Shared.CancelAllAndDrainAsync()
                .Wait(TimeSpan.FromSeconds(10));
        }
        catch { /* 超时或聚合异常：进程即将退出，续传日志按最近一次节流落盘恢复 */ }
        base.OnExit(e);
    }
}
