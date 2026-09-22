using System.Text.RegularExpressions;
using System.Windows;

namespace author.Presentation.Views;

/// <summary>新建数据生成器的命名/描述弹窗。保存后关闭，返回名称与描述。</summary>
public partial class NewGeneratorWindow : Window
{
    private readonly Func<string, bool> _nameExists;

    /// <summary>保存成功后返回的生成器名称。</summary>
    public string? GenName { get; private set; }

    /// <summary>保存成功后返回的生成器描述（「无」视为空）。</summary>
    public string? GenDesc { get; private set; }

    public NewGeneratorWindow(Func<string, bool> nameExists)
    {
        InitializeComponent();
        _nameExists = nameExists;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name == "无")
        {
            MessageBox.Show(this, "名称不能为空，也不能为「无」。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_\-]+$"))
        {
            MessageBox.Show(this, "名称只能包含字母、数字、下划线和横线。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_nameExists(name))
        {
            MessageBox.Show(this, $"生成器「{name}」已存在，请换一个名字。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string desc = DescBox.Text.Trim();
        if (desc == "无") desc = "";
        GenName = name;
        GenDesc = desc;
        DialogResult = true;
    }
}
