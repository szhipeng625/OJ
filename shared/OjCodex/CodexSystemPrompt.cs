using System.Text;

namespace OjCodex;

/// <summary>
/// system prompt 的拼装。缓存友好是硬要求，因此这里的每个方法都只产出「稳定文本」：
///
///   [稳定块 1] 角色 + 工具规约 + 思考链 + 输出规范   ← 只随用户改设置而变，打缓存断点
///   [稳定块 2] 题库快照（题目清单 + 标签表）          ← 只在题库变动时变，再打一个断点
///
/// 题目正文、用户指令这类每次都不同的内容，一律放在 messages 里（断点之后）。
/// 千万别把时间戳、题号之类的变量拼进这里 —— 那会让整段前缀缓存全部失效。
/// </summary>
public static class CodexSystemPrompt
{
    /// <summary>稳定块 1：角色与规约。</summary>
    public static string BuildStable(CodexSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 ACMDOGCodex，ACMDOG 在线判题系统的出题助手，嵌入在「出题工作台（服务端）」里。");
        sb.AppendLine("你的主场是题面：审校、补全、重写题面，让它对做题人没有歧义。");
        sb.AppendLine();
        sb.AppendLine("<rules>");
        sb.AppendLine("1. 结论先行：先说要改什么、为什么，再动手；不要复述整篇题面。");
        sb.AppendLine("2. 改题面之前必须先调 oj_get_problem 看原文，不要凭印象改。");
        sb.AppendLine("3. 只改有问题的部分。没坏的地方保持原样，不要顺手重写。");
        sb.AppendLine("4. 数据范围必须显式写出上下界；边界、多解、精度要求都要在题面里交代清楚。");
        sb.AppendLine("5. 新标签先查 oj_list_tags，尽量复用已有标签的写法，不要造同义标签。");
        sb.AppendLine("6. 写回题面用 oj_save_statement，传完整正文而非差异片段。");
        sb.AppendLine("7. 一次只改一道题。用户没点名题目时，先用 oj_list_problems 列出题目让用户选。");
        sb.AppendLine("8. 不要在回答里输出没有依据的判断。不确定的地方，去调工具确认。");
        sb.AppendLine("</rules>");
        sb.AppendLine();
        sb.Append(ThinkingChain.Render(settings.ThinkingChain));
        sb.AppendLine();
        sb.AppendLine("<output>");
        sb.AppendLine("用中文回答。改动清单用短列表，每条写清「原文 → 改成什么 → 为什么」。");
        sb.AppendLine("涉及时限/内存/数据范围时给出具体数字，不要写「适当」「合理」这类模糊词。");
        sb.AppendLine("</output>");
        return sb.ToString();
    }

    /// <summary>稳定块 2：题库快照。只在题库变动时重算，配合第二个缓存断点。</summary>
    public static string BuildBankSnapshot(IReadOnlyList<ProblemBrief> problems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<problem_bank>");
        sb.AppendLine($"题库共 {problems.Count} 道题：");

        var tagCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in problems.OrderBy(p => p.Id))
        {
            string tags = p.Tags.Length > 0 ? string.Join("/", p.Tags) : "无标签";
            sb.AppendLine($"  P{p.Id} {p.Title}  [{tags}]{(p.IsPublic ? "" : " （未公开）")}");
            foreach (var t in p.Tags)
                tagCount[t] = tagCount.GetValueOrDefault(t) + 1;
        }

        if (tagCount.Count > 0)
        {
            sb.AppendLine("已用标签（出现次数）：");
            sb.AppendLine("  " + string.Join("、", tagCount.OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}×{kv.Value}")));
        }

        sb.AppendLine("</problem_bank>");
        return sb.ToString();
    }

    /// <summary>
    /// 检索出来的相关题目摘要。刻意放在 user 消息里而不是 system 稳定块里 ——
    /// 它随指令变化，塞进 system 会让每条指令的缓存前缀都失效，得不偿失。
    /// </summary>
    public static string BuildRetrievedContext(IReadOnlyList<ProblemSearchDoc> docs, int snippetChars)
    {
        if (docs.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("<retrieved_problems note=\"按本指令检索出的最相关题目；system 的 problem_bank 里有全量索引。需要完整题面再调 oj_get_problem。\">");
        foreach (var d in docs)
        {
            string text = d.Text;
            if (text.Length > snippetChars) text = text[..snippetChars] + "…";
            sb.AppendLine($"  · {text}");
        }
        sb.AppendLine("</retrieved_problems>");
        return sb.ToString();
    }

    /// <summary>首条 user 消息：用户指令 + （可选）检索上下文 + （可选）当前聚焦的题目正文。每次都不同，放在断点之后。</summary>
    public static string BuildTaskMessage(string instruction, ProblemDoc? focus,
        IReadOnlyList<ProblemSearchDoc>? retrieved = null, int snippetChars = 240)
    {
        if (focus is null && (retrieved is null || retrieved.Count == 0)) return instruction;

        var sb = new StringBuilder();

        if (retrieved is { Count: > 0 })
        {
            sb.Append(BuildRetrievedContext(retrieved, snippetChars));
            sb.AppendLine();
        }

        if (focus is null)
        {
            sb.AppendLine("<instruction>");
            sb.AppendLine(instruction);
            sb.AppendLine("</instruction>");
            return sb.ToString();
        }
        sb.AppendLine($"<current_problem id=\"P{focus.Id}\">");
        sb.AppendLine($"标题：{focus.Title}");
        sb.AppendLine($"时限：{focus.TimeMs}ms  内存：{focus.MemMb}MB  标签：{(focus.Tags.Length > 0 ? string.Join("/", focus.Tags) : "无")}");
        sb.AppendLine("当前题面：");
        sb.AppendLine(focus.Description);
        if (!string.IsNullOrWhiteSpace(focus.SampleIn) || !string.IsNullOrWhiteSpace(focus.SampleOut))
        {
            sb.AppendLine("样例输入：");
            sb.AppendLine(focus.SampleIn);
            sb.AppendLine("样例输出：");
            sb.AppendLine(focus.SampleOut);
        }
        sb.AppendLine("</current_problem>");
        sb.AppendLine();
        sb.AppendLine("<instruction>");
        sb.AppendLine(instruction);
        sb.AppendLine("</instruction>");
        return sb.ToString();
    }
}
