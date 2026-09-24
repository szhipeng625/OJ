using System.Windows;
using client.Business.Services;

namespace client.Presentation.Views;

public partial class RegisterWindow : Window
{
    private readonly AuthService _auth;

    /// <summary>注册成功后返回的用户名（供登录窗口回填）。</summary>
    public string? RegisteredUsername { get; private set; }

    public RegisterWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        UserBox.Focus();
    }

    private async void RegBtn_OnClick(object sender, RoutedEventArgs e)
    {
        string u = UserBox.Text.Trim();
        string p = PassBox.Password;
        string email = EmailBox.Text.Trim();
        string name = NameBox.Text.Trim();
        string school = SchoolBox.Text.Trim();

        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
        {
            ErrText.Text = "请输入用户名和密码";
            return;
        }
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(school))
        {
            ErrText.Text = "请填写邮箱、姓名和学校";
            return;
        }

        RegBtn.IsEnabled = false;
        ErrText.Text = "注册中...";
        try
        {
            bool ok = await _auth.RegisterAsync(u, p, email, name, school);
            if (ok)
            {
                RegisteredUsername = u;
                DialogResult = true;
            }
            else
            {
                ErrText.Text = "注册失败，用户名可能已存在";
            }
        }
        finally { RegBtn.IsEnabled = true; }
    }

    private void CancelBtn_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
