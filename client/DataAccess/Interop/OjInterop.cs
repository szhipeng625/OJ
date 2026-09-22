using System.Runtime.InteropServices;
using System.Text;

namespace client.DataAccess.Interop;

/// <summary>
/// 直接 P/Invoke 调用 C++ 判题核心 ojcore.dll（替代原 Go HTTP 服务）。
/// </summary>
public static class OJInterop
{
    private const string Dll = "ojcore.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int oj_init([MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir,
                                      [MarshalAs(UnmanagedType.LPUTF8Str)] string data_dir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int oj_init_redis([MarshalAs(UnmanagedType.LPUTF8Str)] string host, int port);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_get_problems();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_get_user_solution(int problem_id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username, int contest_id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_get_user_progress(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_get_user_contest_progress(int cid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_submit(int problem_id, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_submit_ex(int problem_id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string code,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        int virtual_);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_get_contests();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_get_contest(int cid);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_get_board(int cid);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_submit_contest(int problem_id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string code,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        int virtual_, int contest_id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_contest_register(int cid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username, int virtual_);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_contest_registration(int cid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_contest_submissions(int cid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username, int view_all);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int oj_init_mysql([MarshalAs(UnmanagedType.LPUTF8Str)] string host, int port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data_dir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_init_schema();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_sync_problems();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_register([MarshalAs(UnmanagedType.LPUTF8Str)] string u,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string p,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string role);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_login([MarshalAs(UnmanagedType.LPUTF8Str)] string u,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_whoami([MarshalAs(UnmanagedType.LPUTF8Str)] string token);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_update_profile([MarshalAs(UnmanagedType.LPUTF8Str)] string token,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string nickname,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string avatar);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_logout([MarshalAs(UnmanagedType.LPUTF8Str)] string token);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_list_users([MarshalAs(UnmanagedType.LPUTF8Str)] string token);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void oj_free_string(IntPtr s);

    private static string PtrToString(IntPtr p)
    {
        if (p == IntPtr.Zero) return "[]";
        try { return Marshal.PtrToStringUTF8(p) ?? "[]"; }
        finally { oj_free_string(p); }
    }

    /// <summary>初始化引擎。problemDir 题目目录，dataDir 数据/临时目录。</summary>
    public static void Init(string problemDir, string dataDir)
        => oj_init(problemDir, dataDir);

    /// <summary>连接远端 LSM 存储（RESP，Redis 协议，默认 6379）。失败不阻塞启动。</summary>
    public static void InitRedis(string host, int port)
        => oj_init_redis(host, port);

    public static string GetProblemsJson() => PtrToString(oj_get_problems());

    public static string GetUserSolutionJson(int problemId, string username, int contestId)
        => PtrToString(oj_get_user_solution(problemId, username, contestId));

    public static string GetUserProgressJson(string username)
        => PtrToString(oj_get_user_progress(username));

    public static string GetUserContestProgressJson(int contestId, string username)
        => PtrToString(oj_get_user_contest_progress(contestId, username));

    public static string SubmitJson(int problemId, string code)
        => PtrToString(oj_submit(problemId, code));

    public static string SubmitExJson(int problemId, string code, string username, bool virt)
        => PtrToString(oj_submit_ex(problemId, code, username, virt ? 1 : 0));

    public static string GetContestsJson() => PtrToString(oj_get_contests());

    public static string GetContestJson(int cid) => PtrToString(oj_get_contest(cid));

    public static string GetBoardJson(int cid) => PtrToString(oj_get_board(cid));

    public static string SubmitContestJson(int problemId, string code, string username, bool virt, int contestId)
        => PtrToString(oj_submit_contest(problemId, code, username, virt ? 1 : 0, contestId));

    public static string ContestRegisterJson(int cid, string username, bool virt)
        => PtrToString(oj_contest_register(cid, username, virt ? 1 : 0));

    public static string ContestRegistrationJson(int cid, string username)
        => PtrToString(oj_contest_registration(cid, username));

    public static string ContestSubmissionsJson(int cid, string username, bool viewAll)
        => PtrToString(oj_contest_submissions(cid, username, viewAll ? 1 : 0));

    public static int InitMySQL(string host, int port, string user, string pass, string db, string problemDir, string dataDir)
        => oj_init_mysql(host, port, user, pass, db, problemDir, dataDir);

    public static string InitMysqlSchemaJson() => PtrToString(oj_mysql_init_schema());
    public static string SyncProblemsJson() => PtrToString(oj_mysql_sync_problems());
    public static string RegisterJson(string u, string p, string role) => PtrToString(oj_register(u, p, role));
    public static string LoginJson(string u, string p) => PtrToString(oj_login(u, p));
    public static string WhoamiJson(string token) => PtrToString(oj_whoami(token));
    public static string UpdateProfileJson(string token, string nickname, string avatar)
        => PtrToString(oj_update_profile(token, nickname, avatar));
    public static string LogoutJson(string token) => PtrToString(oj_logout(token));
    public static string ListUsersJson(string token) => PtrToString(oj_list_users(token));
}
