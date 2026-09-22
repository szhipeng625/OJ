using System.Runtime.InteropServices;

namespace author.DataAccess.Interop;

/// <summary>
/// ojcore.dll（判题核心，内含 MySQL 连接等进程级全局状态）的 P/Invoke 声明。
/// ojcore 的连接/句柄是进程内单例，并非为并发调用设计，因此这里提供 <see cref="Lock"/>，
/// 所有 DAL 客户端对 ojcore 的调用都在该锁内串行执行（调用都很快；耗时的编译/评测走 authorcore）。
/// </summary>
public static class OjCoreInterop
{
    private const string Dll = "ojcore.dll";

    /// <summary>串行化所有 ojcore 调用，保护其内部全局 MySQL 状态。</summary>
    public static readonly object Lock = new();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int oj_init_mysql([MarshalAs(UnmanagedType.LPUTF8Str)] string host, int port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data_dir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_login([MarshalAs(UnmanagedType.LPUTF8Str)] string u,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_whoami([MarshalAs(UnmanagedType.LPUTF8Str)] string token);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_init_schema();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_publish_problem(int id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir, int is_public);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_problem_visibility();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_list_generators();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_get_generator(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_create_generator([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string code, [MarshalAs(UnmanagedType.LPUTF8Str)] string description);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_update_generator(int id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string code, [MarshalAs(UnmanagedType.LPUTF8Str)] string description);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_problem_generators(int problem_id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_bind_generator(int problem_id, int generator_id, int gen_count);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_unbind_generator(int problem_id, int generator_id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_search_generators([MarshalAs(UnmanagedType.LPUTF8Str)] string keyword);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_publish_contest(int cid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string contest_json);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void oj_free_string(IntPtr s);

    internal static string Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int len = 0; while (Marshal.ReadByte(p, len) != 0) len++;
        var bytes = new byte[len];
        Marshal.Copy(p, bytes, 0, len);
        oj_free_string(p);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // ===== 认证 / 初始化 =====
    public static int InitMysql(string host, int port, string user, string pass, string db, string problemDir, string dataDir)
        => oj_init_mysql(host, port, user, pass, db, problemDir, dataDir);

    public static string InitSchema()
    {
        lock (Lock) return Take(oj_mysql_init_schema());
    }

    // ===== 题目 / 比赛发布到 MySQL =====
    public static string PublishProblemDir(int id, string problemDir, bool isPublic)
    {
        lock (Lock) return Take(oj_mysql_publish_problem(id, problemDir, isPublic ? 1 : 0));
    }

    /// <summary>全部题目的公开状态：{"id":true/false,...}。</summary>
    public static string ProblemVisibilityJson()
    {
        lock (Lock) return Take(oj_mysql_problem_visibility());
    }

    // ===== 生成器库（全局 generators 表） =====
    public static string ListGeneratorsJson()
    {
        lock (Lock) return Take(oj_mysql_list_generators());
    }

    public static string GetGeneratorJson(int id)
    {
        lock (Lock) return Take(oj_mysql_get_generator(id));
    }

    public static string CreateGeneratorJson(string name, string code, string desc)
    {
        lock (Lock) return Take(oj_mysql_create_generator(name, code, desc));
    }

    public static string UpdateGeneratorJson(int id, string code, string desc)
    {
        lock (Lock) return Take(oj_mysql_update_generator(id, code, desc));
    }

    public static string ProblemGeneratorsJson(int problemId)
    {
        lock (Lock) return Take(oj_mysql_problem_generators(problemId));
    }

    public static string BindGeneratorJson(int problemId, int generatorId, int genCount)
    {
        lock (Lock) return Take(oj_mysql_bind_generator(problemId, generatorId, genCount));
    }

    public static string UnbindGeneratorJson(int problemId, int generatorId)
    {
        lock (Lock) return Take(oj_mysql_unbind_generator(problemId, generatorId));
    }

    public static string SearchGeneratorsJson(string keyword)
    {
        lock (Lock) return Take(oj_mysql_search_generators(keyword));
    }

    public static string PublishContest(int cid, string contestJson)
    {
        lock (Lock) return Take(oj_mysql_publish_contest(cid, contestJson));
    }

    public static string LoginJson(string u, string p)
    {
        lock (Lock) return Take(oj_login(u, p));
    }

    public static string WhoamiJson(string token)
    {
        lock (Lock) return Take(oj_whoami(token));
    }
}
