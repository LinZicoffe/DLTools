using System.Windows;

namespace 烧录上位机程序.Pages;

public partial class AuthorizationPasswordWindow : Window
{
    private readonly bool _confirmPassword;
    private string? _password;

    public AuthorizationPasswordWindow(bool confirmPassword)
    {
        InitializeComponent();
        _confirmPassword = confirmPassword;
        Prompt.Text = confirmPassword ? "设置加密备份密码" : "输入备份密码";
        ConfirmationPanel.Visibility = confirmPassword ? Visibility.Visible : Visibility.Collapsed;
        ContentRendered += (_, _) => PasswordInput.Focus();
        Closed += (_, _) => { PasswordInput.Clear(); ConfirmationInput.Clear(); };
    }

    public string TakePassword()
    {
        var value = _password ?? throw new InvalidOperationException("尚未确认密码。");
        _password = null;
        return value;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Password;
        if (string.IsNullOrWhiteSpace(password) || password.Length < 16 || password.Length > 1024)
        {
            ErrorText.Text = "请输入 16～1024 个字符的独立强密码。";
            return;
        }
        if (_confirmPassword && password != ConfirmationInput.Password)
        {
            ErrorText.Text = "两次输入的密码不一致。";
            return;
        }
        _password = password;
        DialogResult = true;
    }
}
