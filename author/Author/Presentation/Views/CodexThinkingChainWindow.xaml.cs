using System.Windows;
using OjCodex;

namespace author.Presentation.Views;

/// <summary>编辑 Codex 的思考逻辑链（写进 config.xml 的 &lt;codex&gt;&lt;thinkingChain&gt;）。</summary>
public partial class CodexThinkingChainWindow : Window
{
    /// <summary>保存后的思考链文本（用户点了取消则为 null）。</summary>
    public string? Result { get; private set; }

    public CodexThinkingChainWindow(string current)
    {
        InitializeComponent();
        ChainBox.Text = ThinkingChain.Effective(current);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = ChainBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void OnResetDefault(object sender, RoutedEventArgs e)
        => ChainBox.Text = ThinkingChain.Default;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
