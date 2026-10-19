using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OfflineAuthorization.Demo.Core;

namespace OfflineAuthorization.WinForms;

public sealed class MainForm : Form
{
    private readonly DemoAuthorizationClient _client;
    private DemoSnapshot? _snapshot;
    private bool _exporting;
    private readonly TextBox _challenge = new() { Name = "ChallengeBox", ReadOnly = true, Width = 180, Font = new Font("Consolas", 25) };
    private readonly TextBox _response = new() { Name = "ResponseBox", MaxLength = 6, Width = 180, Font = new Font("Consolas", 25) };
    private readonly Label _status = new() { Name = "StatusText", AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly Button _activate = ActionButton("确认授权", "ActivateButton");
    private readonly Button _renew = ActionButton("更换申请码", "RenewButton");
    private readonly Button _feature = ActionButton("运行受控演示功能", "FeatureButton");
    private readonly Button _export = ActionButton("导出生成器测试档案", "ExportButton");
    private readonly Button _reset = ActionButton("重置测试授权", "ResetButton");

    public MainForm(DemoAuthorizationClient? client = null)
    {
        _client = client ?? new DemoAuthorizationClient("WinForms");
        Text = "WinForms · 六位离线授权 DEMO";
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 740);
        MinimumSize = new Size(820, 680);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(24), ColumnCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(body); Controls.Add(scroll);
        Add(body, new Label { Text = "WinForms 离线授权演示", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true });
        Add(body, TextLabel("全程离线：读取六位申请码 → 在 DL小工具中计算 → 输入六位授权码。"));
        Add(body, TextLabel("1  配对测试档案", bold: true));
        Add(body, TextLabel("首次使用：导出测试档案，在 DL小工具 → 离线授权 → 恢复档案中导入一次。\n选择“" + DemoProfile.DisplayName + "”，不要新建随机档案。"));
        Add(body, _export);
        Add(body, TextLabel("测试备份密码：" + DemoProfile.BackupPassword));
        Add(body, TextLabel("设备标识：" + DemoProfile.DeviceId));
        Add(body, TextLabel("2  提供申请码并输入授权码", bold: true));
        var requestRow = Row(_challenge, ActionButton("复制申请码", "CopyButton"));
        ((Button)requestRow.Controls[1]).Click += (_, _) => Run(() =>
        {
            if (_snapshot is not null) Clipboard.SetText(_snapshot.Challenge);
            _status.Text = "申请码已复制，请在 DL小工具中选择测试档案后计算。";
        });
        Add(body, requestRow);
        Add(body, Row(_response, _activate));
        var refresh = ActionButton("刷新状态", "RefreshButton");
        Add(body, Row(_renew, refresh));
        Add(body, _status);
        Add(body, TextLabel("3  测试授权后的功能", bold: true));
        Add(body, _feature);
        Add(body, Row(_reset));
        Add(body, TextLabel("仅供测试：使用公开测试密钥。重置会清除本 DEMO 的激活和限次，不能作为正式产品入口。\n两套 DEMO 的状态独立保存，重启会保留；不会修改 DL小工具的真实档案。"));
        Add(body, TextLabel("测试状态位置：" + _client.StatePath));
        _activate.Click += (_, _) => Run(() =>
        {
            var result = _client.Activate(_response.Text);
            Render(result.Snapshot);
            _response.Clear();
            _status.Text = result.Message + "\n" + result.Snapshot.Status;
        });
        _renew.Click += (_, _) => Run(() => { Render(_client.RenewChallenge()); _response.Clear(); });
        refresh.Click += (_, _) => Reload();
        _reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "仅重置本 WinForms DEMO 的测试授权、申请历史和失败次数，旧状态保留备份。是否继续？",
                "重置测试授权", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Run(() => { Render(_client.ResetForTesting()); _response.Clear(); });
        };
        _feature.Click += (_, _) => Run(() =>
        {
            Render(_client.Load()); // Recheck persistent authorization at the actual protected action.
            if (_snapshot!.IsActivated)
                MessageBox.Show(this, "WinForms 受控功能已执行。离线授权验证成功。", "演示功能");
        });
        _export.Click += async (_, _) => await ExportAsync();
        Shown += (_, _) => Reload();
        FormClosing += (_, e) =>
        {
            if (_exporting) { e.Cancel = true; _status.Text = "测试档案正在保存，请完成后再关闭。"; }
        };
    }

    private void Reload() => Run(() => Render(_client.Load()));
    private void Render(DemoSnapshot snapshot)
    {
        _snapshot = snapshot;
        _challenge.Text = snapshot.Challenge;
        _status.Text = snapshot.Status;
        UpdateControls();
    }

    private void UpdateControls()
    {
        _activate.Enabled = _response.Enabled = _renew.Enabled = !_exporting && _snapshot?.CanActivate == true;
        _feature.Enabled = !_exporting && _snapshot?.IsActivated == true;
        _export.Enabled = _reset.Enabled = !_exporting;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or CryptographicException or ExternalException)
        {
            _snapshot = null;
            _status.Text = error.Message;
            UpdateControls();
        }
    }

    private async Task ExportAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "测试档案 (*.dlauth)|*.dlauth", FileName = "DEMO公开测试档案.dlauth", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _exporting = true; UpdateControls();
        try
        {
            await DemoProfile.ExportAsync(dialog.FileName);
            _status.Text = "测试档案已导出。请在 DL小工具中恢复该档案，密码见上方。客户输入授权时无需文件。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { _status.Text = error.Message; }
        finally { _exporting = false; UpdateControls(); }
    }

    private static Button ActionButton(string text, string name) => new()
    { Text = text, Name = name, AutoSize = true, MinimumSize = new Size(116, 38), Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 3, 12, 3) };
    private Label TextLabel(string text, bool bold = false) => new()
    { Text = text, AutoSize = true, MaximumSize = new Size(760, 0), Font = bold ? new Font(Font, FontStyle.Bold) : Font };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        row.Controls.AddRange(controls); return row;
    }
    private static void Add(TableLayoutPanel panel, Control control)
    {
        control.Margin = new Padding(0, 0, 0, 12);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.Controls.Add(control, 0, panel.RowCount++);
    }
}
