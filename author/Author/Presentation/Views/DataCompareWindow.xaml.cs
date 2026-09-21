using System.Windows;
using author.DataAccess.Models;

namespace author.Presentation.Views;

/// <summary>左右两栏对比某组测试数据的输入（.in）与输出（.out）。</summary>
public partial class DataCompareWindow : Window
{
    public DataCompareWindow(DataRow row, string inText, bool inTrunc, string outText, bool outTrunc)
    {
        InitializeComponent();
        Title = $"数据对比：{row.InFile} ⇄ {row.OutFile}";
        TitleText.Text = $"{row.GroupTitle}  ·  第 {row.BaseName} 组";
        InTitle.Text = "输入数据 " + row.InFile + (inTrunc ? "（内容过大，仅显示前 200KB）" : "");
        OutTitle.Text = row.HasOut
            ? "输出数据 " + row.OutFile + (outTrunc ? "（内容过大，仅显示前 200KB）" : "")
            : "输出数据（缺失，尚未生成标准答案）";
        InBox.Text = inText;
        OutBox.Text = outText;
    }
}
