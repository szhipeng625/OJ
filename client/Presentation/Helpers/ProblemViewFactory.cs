using System.Collections.Generic;
using System.Linq;
using client.DataAccess.Models;
using OjUiControls.Models;

namespace client.Presentation.Helpers;

/// <summary>
/// 把客户端的题目模型映射为共享只读题面窗口（OjUiControls.Views.ProblemViewerWindow）的数据载体，
/// 避免共享层反向依赖 client 的数据模型。
/// </summary>
public static class ProblemViewFactory
{
    public static ProblemViewData Create(Problem p, string primaryActionText = "开始做题")
    {
        IReadOnlyList<ProblemSample>? samples = p.Samples?
            .Select(s => new ProblemSample(s.Name, s.Input, s.Output, s.IsSample))
            .ToList();

        string tags = p.Tags is { Length: > 0 } ? string.Join(", ", p.Tags) : "无";
        string meta = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB · 标签：{tags}";

        return ProblemViewData.From(p.Id, p.Title, p.Description, samples,
            p.SampleIn, p.SampleOut, meta, primaryActionText);
    }
}
