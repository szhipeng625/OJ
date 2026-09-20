using System.Windows;
using System.Windows.Controls;
using client.Business.Services;
using client.DataAccess.Models;

namespace client.Presentation.Views;

public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    public LoginResult? Session { get; private set; }

    public LoginWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        UserBox.Focus();
    }

    private async void LoginBtn_OnClick(object sender, RoutedEventArgs e)
    {
        string u = UserBox.Text.Trim();
        string p = PassBox.Password;
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
        {
            ErrText.Text = "请输入用户名和密码";
            return;
        }
        LoginBtn.IsEnabled = false; RegBtn.IsEnabled = false;
        ErrText.Text = "登录中...";
        try
        {
            var r = await _auth.LoginAsync(u, p);
            if (r is { Ok: true })
            {
                Session = r;
                DialogResult = true;
            }
            else
            {
                ErrText.Text = r?.Error ?? "登录失败";
            }
        }
        finally { LoginBtn.IsEnabled = true; RegBtn.IsEnabled = true; }
    }

    private async void RegBtn_OnClick(object sender, RoutedEventArgs e)
    {
        string u = UserBox.Text.Trim();
        string p = PassBox.Password;
        if (u.Length < 2) { ErrText.Text = "用户名至少 2 个字符"; return; }
        if (p.Length < 6) { ErrText.Text = "密码至少 6 位"; return; }
        string role = ((ComboBoxItem)RoleBox.SelectedItem).Content.ToString()!.Split('（')[0];
        LoginBtn.IsEnabled = false; RegBtn.IsEnabled = false;
        ErrText.Text = "注册中...";
        try
        {
            bool ok = await _auth.RegisterAsync(u, p, role);
            ErrText.Text = ok ? "注册成功，请点登录" : "注册失败（用户名可能已存在）";
        }
        finally { LoginBtn.IsEnabled = true; RegBtn.IsEnabled = true; }
    }
}
