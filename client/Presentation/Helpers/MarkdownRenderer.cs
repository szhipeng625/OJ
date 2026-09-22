using System.Windows.Controls;
using Markdig;
using client.DataAccess.Models;

namespace client.Presentation.Helpers;

/// <summary>
/// UI 辅助：题面 Markdown 转 HTML 并渲染到 WebBrowser（统一样式）。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().Build();

    /// <summary>把题面描述与测试样例拼成完整 Markdown（样例直接放进题面里展示）。</summary>
    public static string CombineStatement(string description, List<TestCaseSample>? samples, string sampleIn, string sampleOut)
    {
        var sb = new System.Text.StringBuilder(description ?? "");
        if (samples is { Count: > 0 })
        {
            foreach (var s in samples)
            {
                string label = s.IsSample ? "样例" : "测试用例";
                sb.Append($"\n\n## {label}：{s.Name}\n\n");
                if (!string.IsNullOrWhiteSpace(s.Input))
                    sb.Append("**输入**\n\n```\n").Append(s.Input).Append("\n```\n");
                if (!string.IsNullOrWhiteSpace(s.Output))
                    sb.Append("**输出**\n\n```\n").Append(s.Output).Append("\n```\n");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(sampleIn))
                sb.Append("\n\n## 样例输入\n\n```\n").Append(sampleIn).Append("\n```");
            if (!string.IsNullOrWhiteSpace(sampleOut))
                sb.Append("\n\n## 样例输出\n\n```\n").Append(sampleOut).Append("\n```");
        }
        return sb.ToString();
    }

    public static void Render(WebBrowser wb, string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            wb.NavigateToString("<html><body></body></html>");
            return;
        }

        string body = Markdown.ToHtml(markdown, Pipeline);
        string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
            + "<style>body{font-family:'Segoe UI',Microsoft YaHei,sans-serif;font-size:17px;line-height:1.7;font-weight:600;color:#1f2d3d;margin:14px;}"
            + "h1{font-size:26px;font-weight:700;border-bottom:2px solid #3498db;padding-bottom:6px;}"
            + "h2{font-size:22px;font-weight:700;border-bottom:1px solid #bdc3c7;padding-bottom:4px;}"
            + "h3{font-size:19px;font-weight:700;}code{background:#f0f3f7;padding:2px 5px;border-radius:3px;font-family:Consolas,monospace;font-size:15px;font-weight:600;color:#c0392b;}"
            + "pre{background:#f0f3f7;padding:10px;border-radius:5px;overflow-x:auto;}"
            + "pre code{background:none;padding:0;font-weight:600;color:#2c3e50;}"
            + "blockquote{border-left:4px solid #3498db;margin:8px 0;padding:6px 12px;background:#f8f9fa;color:#555;}"
            + "table{border-collapse:collapse;margin:8px 0;}th,td{border:1px solid #bdc3c7;padding:5px 10px;}"
            + "th{background:#ecf0f1;}ul,ol{margin:6px 0;padding-left:24px;}"
            + "a{color:#3498db;text-decoration:none;}hr{border:none;border-top:1px solid #bdc3c7;margin:12px 0;}"
            + "</style></head><body>" + body + "</body></html>";
        wb.NavigateToString(html);
    }
}
