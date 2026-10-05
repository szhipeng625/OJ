using System.Collections.Generic;

namespace OjUiControls.Models;

/// <summary>题面里的一段测试样例（只读展示用，与各端自有模型解耦）。</summary>
public record ProblemSample(string Name, string Input, string Output, bool IsSample);

/// <summary>
/// 只读题面窗口的数据载体：由各端从自有题目模型映射而来，
/// 共享层因此无需反向依赖 client / author 的数据模型。
/// </summary>
public record ProblemViewData(
    int Id,
    string Title,
    string StatementMarkdown,
    string MetaText,
    string PrimaryActionText = "开始做题")
{
    /// <summary>从题面描述与样例拼出待渲染的完整 Markdown。</summary>
    public static ProblemViewData From(int id, string title, string description,
        IReadOnlyList<ProblemSample>? samples, string sampleIn, string sampleOut,
        string metaText, string primaryActionText = "开始做题")
        => new(id, title, Helpers.MarkdownRenderer.CombineStatement(description, samples, sampleIn, sampleOut),
            metaText, primaryActionText);
}
