using System.Text;

namespace OjCodex;

/// <summary>
/// 可自定义的「思考逻辑链」：一段用户可编辑的固定步骤说明，渲染进 system prompt。
///
/// 两条硬约束（都是为了不破坏 prompt cache 的前缀）：
///   1. 它只随「用户编辑」变化，绝不掺入题号、时间戳等每次都会变的内容；
///   2. 它位于缓存断点之前的稳定区，题目内容一律放在其后的首条 user 消息里。
/// </summary>
public static class ThinkingChain
{
    /// <summary>默认思考链：面向「审校并重写题面」这一主要任务。</summary>
    public const string Default = """
        1. 读懂题意：从现有题面里抽出——背景、输入格式、输出格式、数据范围、样例。缺哪一项，明确记为"缺失"。
        2. 找问题：逐条排查——数据范围是否给出、边界是否说明、表述是否有歧义、样例与描述是否自相矛盾、
           是否存在多解而题面未加限制、时限/内存是否与算法复杂度匹配。
        3. 定方案：先想清楚改动清单（要补什么、要改哪句、要不动什么），再动笔；不要顺手重写没坏的部分。
        4. 写题面：统一结构为 题目背景 → 输入格式 → 输出格式 → 数据范围 → 样例说明；
           公式与变量名沿用社区惯例，样例必须与输入输出严格一致。
        5. 自检：拿题面里的样例在心里跑一遍，确认描述与样例不矛盾；确认数据范围能覆盖标程的复杂度；
           确认标签与题目实际考点一致。
        6. 落盘：调用 oj_save_statement 写回题面，然后向用户汇报「改了哪几处、为什么」，
           不要复述整篇题面。
        """;

    /// <summary>取生效的思考链（用户自定义优先）。</summary>
    public static string Effective(string? custom) =>
        string.IsNullOrWhiteSpace(custom) ? Default : custom.Trim();

    /// <summary>把思考链解析成步骤列表，供面板渲染成清单。</summary>
    public static IReadOnlyList<string> ParseSteps(string? chain)
    {
        var text = Effective(chain);
        var steps = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // 只认「数字. 」开头的行作为步骤标题
            int i = 0;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i > 0 && i < line.Length && (line[i] == '.' || line[i] == '、'))
            {
                var body = line[(i + 1)..].Trim();
                int colon = body.IndexOf('：');
                if (colon < 0) colon = body.IndexOf(':');
                steps.Add(colon > 0 ? body[..colon] : body);
            }
        }
        return steps.Count > 0 ? steps : ["(当前思考链没有编号步骤)"];
    }

    /// <summary>渲染成 system prompt 里的一个固定小节。</summary>
    public static string Render(string? custom)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<thinking_chain>");
        sb.AppendLine("每次接到任务，按下面这条固定链路推进，不要跳步：");
        sb.AppendLine(Effective(custom));
        sb.AppendLine("</thinking_chain>");
        return sb.ToString();
    }
}
