using System.Runtime.InteropServices;
using System.Text.Json;

namespace author.Services;

/// <summary>
/// 题目数据生成器存储：LSM 存历史版本，MySQL 存最新版本（重复保存原地替换）。
/// 直接 P/Invoke ojcore.dll，依赖 UserAuth.Init 已完成 oj_init_mysql（LSM+MySQL 初始化）。
/// </summary>
public static class GenStorage
{
    private const string Dll = "ojcore.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_gen_save(int problem_id, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_gen_get_current(int problem_id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_gen_list_versions(int problem_id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_gen_get_version(int problem_id, int version);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_gen_search([MarshalAs(UnmanagedType.LPUTF8Str)] string keyword);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void oj_free_string(IntPtr s);

    private static string PtrToUtf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int len = 0; while (Marshal.ReadByte(p, len) != 0) len++;
        var bytes = new byte[len];
        Marshal.Copy(p, bytes, 0, len);
        oj_free_string(p);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public record GenCurrent(bool Ok, string Code, int Version, string UpdatedAt);
    public record GenVersionItem(int Version, string Ts, int Lines, string Summary);
    public record GenVersionContent(bool Ok, string Code, int Version, string Ts);
    public record GenSearchItem(int ProblemId, int Version, string UpdatedAt, string Preview);

    /// <summary>保存生成器代码：写 LSM 历史版本 + MySQL upsert 最新版本。返回新版本号。</summary>
    public static int Save(int problemId, string code)
    {
        try
        {
            var j = JsonDocument.Parse(PtrToUtf8(oj_gen_save(problemId, code ?? ""))).RootElement;
            return j.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        }
        catch { return 0; }
    }

    /// <summary>读取某题最新生成器代码（优先 MySQL，回退 LSM）。</summary>
    public static GenCurrent GetCurrent(int problemId)
    {
        try
        {
            var j = JsonDocument.Parse(PtrToUtf8(oj_gen_get_current(problemId))).RootElement;
            bool ok = j.GetProperty("ok").GetBoolean();
            return new GenCurrent(
                ok,
                ok && j.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                ok && j.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                ok && j.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "");
        }
        catch { return new GenCurrent(false, "", 0, ""); }
    }

    /// <summary>列出某题全部历史版本（从 LSM），按版本号降序。</summary>
    public static List<GenVersionItem> ListVersions(int problemId)
    {
        var list = new List<GenVersionItem>();
        try
        {
            var j = JsonDocument.Parse(PtrToUtf8(oj_gen_list_versions(problemId))).RootElement;
            foreach (var el in j.EnumerateArray())
            {
                list.Add(new GenVersionItem(
                    el.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    el.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "",
                    el.TryGetProperty("lines", out var l) ? l.GetInt32() : 0,
                    el.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : ""));
            }
        }
        catch { }
        return list;
    }

    /// <summary>读取某题指定历史版本的代码（从 LSM）。</summary>
    public static GenVersionContent GetVersion(int problemId, int version)
    {
        try
        {
            var j = JsonDocument.Parse(PtrToUtf8(oj_gen_get_version(problemId, version))).RootElement;
            bool ok = j.GetProperty("ok").GetBoolean();
            return new GenVersionContent(
                ok,
                ok && j.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                ok && j.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                ok && j.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "");
        }
        catch { return new GenVersionContent(false, "", 0, ""); }
    }

    /// <summary>跨题查找生成器代码（遍历 MySQL 全部题目的最新代码，大小写不敏感）。</summary>
    public static List<GenSearchItem> Search(string keyword)
    {
        var list = new List<GenSearchItem>();
        try
        {
            var j = JsonDocument.Parse(PtrToUtf8(oj_gen_search(keyword ?? ""))).RootElement;
            foreach (var el in j.EnumerateArray())
            {
                list.Add(new GenSearchItem(
                    el.TryGetProperty("problemId", out var p) ? p.GetInt32() : 0,
                    el.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    el.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "",
                    el.TryGetProperty("preview", out var pr) ? pr.GetString() ?? "" : ""));
            }
        }
        catch { }
        return list;
    }
}
