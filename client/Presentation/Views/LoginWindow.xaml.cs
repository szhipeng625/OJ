using System.Windows;
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
        LoginBtn.IsEnabled = false;
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
        finally { LoginBtn.IsEnabled = true; }
    }

    private void RegisterBtn_OnClick(object sender, RoutedEventArgs e)
    {
        var reg = new RegisterWindow(_auth) { Owner = this };
        if (reg.ShowDialog() == true && reg.RegisteredUsername is { } u)
        {
            UserBox.Text = u;
            PassBox.Clear();
            ErrText.Text = "注册成功，请输入密码登录";
            PassBox.Focus();
        }
    }
}
