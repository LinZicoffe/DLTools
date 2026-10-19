using System.Windows;
using Microsoft.Win32;
using 烧录上位机程序.Services.Authorization;
using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序.Pages;

public partial class OfflineAuthorizationPage : BasePage
{
    public OfflineAuthorizationViewModel ViewModel { get; } = new();

    public OfflineAuthorizationPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var picker = new SaveFileDialog { Title = "保存我方授权档案加密备份", Filter = "授权档案加密备份|*.dlauth",
            DefaultExt = ".dlauth", AddExtension = true, OverwritePrompt = true, FileName = "离线授权档案_" + DateTime.Now.ToString("yyyyMMdd") };
        if (picker.ShowDialog(owner) != true) return;
        var password = new AuthorizationPasswordWindow(confirmPassword: true) { Owner = owner };
        if (password.ShowDialog() != true) return;
        await ViewModel.BackupAsync(picker.FileName, password.TakePassword());
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var picker = new OpenFileDialog { Title = "选择我方授权档案加密备份", Filter = "授权档案加密备份|*.dlauth|所有文件|*.*", CheckFileExists = true };
        if (picker.ShowDialog(owner) != true) return;
        var password = new AuthorizationPasswordWindow(confirmPassword: false) { Owner = owner };
        if (password.ShowDialog() != true) return;
        await ViewModel.RestoreAsync(picker.FileName, password.TakePassword(), ConfirmRestoreAsync);
    }

    private Task<bool> ConfirmRestoreAsync(AuthorizationRestorePlan plan, CancellationToken token) => Dispatcher.InvokeAsync(async () =>
    {
        token.ThrowIfCancellationRequested();
        var owner = Window.GetWindow(this);
        if (owner is MainWindow main && !await main.WaitForCloseDecisionAsync(token)) return false;
        token.ThrowIfCancellationRequested();
        var recovery = plan.PreservesDamagedFile
            ? "当前库无法读取。恢复前会保留原损坏文件副本。\n"
            : "现有其他设备档案会保留；同一设备密钥不会被替换。\n";
        var message = recovery + $"\n新增档案：{plan.NewProfiles}\n更新名称：{plan.RenamedProfiles}\n恢复后总数：{plan.Profiles.Count}\n\n确定合并恢复吗？";
        return MessageBox.Show(owner, message, "确认恢复授权档案", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }).Task.Unwrap();
}
