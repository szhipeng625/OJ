using System.Windows.Controls;
using Markdig;

namespace client.Presentation.Helpers;

/// <summary>
/// UI 辅助：题面 Markdown 转 HTML 并渲染到 WebBrowser（统一样式）。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().Build();

    public static void Render(WebBrowser wb, string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            wb.NavigateToString("<html><body></body></html>");
            return;
        }

        string body = Markdown.ToHtml(markdown, Pipeline);
        string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
            + "<style>body{font-family:'Segoe UI',Microsoft YaHei,sans-serif;font-size:14px;line-height:1.7;color:#2c3e50;margin:12px;}"
            + "h1{font-size:22px;border-bottom:2px solid #3498db;padding-bottom:6px;}"
            + "h2{font-size:18px;border-bottom:1px solid #bdc3c7;padding-bottom:4px;}"
            + "h3{font-size:16px;}code{background:#f0f3f7;padding:2px 5px;border-radius:3px;font-family:Consolas,monospace;font-size:13px;color:#c0392b;}"
            + "pre{background:#f0f3f7;padding:10px;border-radius:5px;overflow-x:auto;}"
            + "pre code{background:none;padding:0;color:#2c3e50;}"
            + "blockquote{border-left:4px solid #3498db;margin:8px 0;padding:6px 12px;background:#f8f9fa;color:#555;}"
            + "table{border-collapse:collapse;margin:8px 0;}th,td{border:1px solid #bdc3c7;padding:5px 10px;}"
            + "th{background:#ecf0f1;}ul,ol{margin:6px 0;padding-left:24px;}"
            + "a{color:#3498db;text-decoration:none;}hr{border:none;border-top:1px solid #bdc3c7;margin:12px 0;}"
            + "</style></head><body>" + body + "</body></html>";
        wb.NavigateToString(html);
    }
}
