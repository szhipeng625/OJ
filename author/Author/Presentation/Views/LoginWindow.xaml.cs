using System.Windows;
using author.Business.Services;
using author.DataAccess.Models;

namespace author.Presentation.Views;

public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    public Session? Session { get; private set; }

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
            if (r.Ok)
            {
                Session = r;
                DialogResult = true;
            }
            else
            {
                ErrText.Text = r.Error;
            }
        }
        finally
        {
            LoginBtn.IsEnabled = true;
        }
    }
}
