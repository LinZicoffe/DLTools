using System;
using System.Windows;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.JLink;

namespace 烧录上位机程序;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        try
        {
            var loader = new DllLoader();
            var ver = loader.GetDLLVersion();
            LoggerService.Info($"JLink_x64.dll 版本: {ver}");
        }
        catch (Exception ex)
        {
            LoggerService.Error($"加载 JLink_x64.dll 失败: {ex.Message}");
            MessageBox.Show(
                $"加载 JLink_x64.dll 失败：\n\n{ex.Message}\n\n请确认已安装 SEGGER J-Link 驱动。",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown(1);
            return;
        }

        ConfigService.Initialize();
        I18nService.LoadLanguage(ConfigService.Get<string>("language") ?? "zh_CN");

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 配置写入有 200ms 防抖，退出前强制落盘避免最后一次修改丢失
        ConfigService.Flush();
        base.OnExit(e);
    }
}
