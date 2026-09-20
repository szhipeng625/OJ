using System.Windows;
using author.Services;

namespace author;

public partial class LoginWindow : Window
{
    public UserAuth.Session? Session { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        UserBox.Focus();
    }

    private void LoginBtn_OnClick(object sender, RoutedEventArgs e)
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
        var r = UserAuth.Login(u, p);
        LoginBtn.IsEnabled = true;

        if (r.Ok && (r.Role == "admin" || r.Role == "author"))
        {
            Session = r;
            DialogResult = true;
        }
        else if (r.Ok)
        {
            ErrText.Text = "当前账号角色为 " + r.Role + "，出题端仅允许 admin/author 登录";
        }
        else
        {
            ErrText.Text = r.Error;
        }
    }
}
