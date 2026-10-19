using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using OfflineAuthorization.Demo.Core;

namespace OfflineAuthorization.Wpf;

public partial class MainWindow : Window
{
    private readonly DemoAuthorizationClient _client;
    private DemoSnapshot? _snapshot;
    private bool _exporting;

    public MainWindow() : this(new DemoAuthorizationClient("WPF")) { }
    public MainWindow(DemoAuthorizationClient client)
    {
        _client = client;
        InitializeComponent();
        PasswordText.Text = "测试备份密码：" + DemoProfile.BackupPassword;
        DeviceText.Text = "设备标识：" + DemoProfile.DeviceId;
        PathText.Text = "测试状态位置：" + client.StatePath;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => Reload();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Reload() => Run(() => Render(_client.Load()));

    private void Activate_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var result = _client.Activate(ResponseBox.Text);
        Render(result.Snapshot); ResponseBox.Clear();
        StatusText.Text = result.Message + "\n" + result.Snapshot.Status;
    });

    private void Renew_Click(object sender, RoutedEventArgs e) => Run(() =>
    { Render(_client.RenewChallenge()); ResponseBox.Clear(); });

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "仅重置本 WPF DEMO 的测试授权、申请历史和失败次数，旧状态保留备份。是否继续？",
            "重置测试授权", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Run(() => { Render(_client.ResetForTesting()); ResponseBox.Clear(); });
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_snapshot is not null) Clipboard.SetText(_snapshot.Challenge);
        StatusText.Text = "申请码已复制，请在 DL小工具中选择测试档案后计算。";
    });

    private void Feature_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        Render(_client.Load());
        if (_snapshot!.IsActivated) MessageBox.Show(this, "WPF 受控功能已执行。离线授权验证成功。", "演示功能");
    });

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "测试档案 (*.dlauth)|*.dlauth", FileName = "DEMO公开测试档案.dlauth", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        _exporting = true; UpdateControls();
        try
        {
            await DemoProfile.ExportAsync(dialog.FileName);
            StatusText.Text = "测试档案已导出。请在 DL小工具中恢复该档案，密码见上方。客户输入授权时无需文件。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { StatusText.Text = error.Message; }
        finally { _exporting = false; UpdateControls(); }
    }

    private void Render(DemoSnapshot snapshot)
    {
        _snapshot = snapshot;
        ChallengeBox.Text = snapshot.Challenge;
        StatusText.Text = snapshot.Status;
        UpdateControls();
    }

    private void UpdateControls()
    {
        ActivateButton.IsEnabled = ResponseBox.IsEnabled = RenewButton.IsEnabled = !_exporting && _snapshot?.CanActivate == true;
        FeatureButton.IsEnabled = !_exporting && _snapshot?.IsActivated == true;
        ExportButton.IsEnabled = ResetButton.IsEnabled = !_exporting;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or CryptographicException or ExternalException)
        { _snapshot = null; StatusText.Text = error.Message; UpdateControls(); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exporting) { e.Cancel = true; StatusText.Text = "测试档案正在保存，请完成后再关闭。"; }
    }
}
