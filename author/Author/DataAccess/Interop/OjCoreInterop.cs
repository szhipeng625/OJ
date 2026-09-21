using System.Runtime.InteropServices;

namespace author.DataAccess.Interop;

/// <summary>
/// ojcore.dll（判题核心，内含 MySQL 连接与 LSM 句柄等进程级全局状态）的 P/Invoke 声明。
/// ojcore 的连接/句柄是进程内单例，并非为并发调用设计，因此这里提供 <see cref="Lock"/>，
/// 所有 DAL 客户端对 ojcore 的调用都在该锁内串行执行（调用都很快；耗时的编译/评测走 authorcore）。
/// </summary>
public static class OjCoreInterop
{
    private const string Dll = "ojcore.dll";

    /// <summary>串行化所有 ojcore 调用，保护其内部全局 MySQL/LSM 状态。</summary>
    public static readonly object Lock = new();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int oj_init_mysql([MarshalAs(UnmanagedType.LPUTF8Str)] string host, int port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_login([MarshalAs(UnmanagedType.LPUTF8Str)] string u,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_whoami([MarshalAs(UnmanagedType.LPUTF8Str)] string token);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oj_mysql_init_schema();

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
    public static int InitMysql(string host, int port, string user, string pass, string db, string problemDir)
        => oj_init_mysql(host, port, user, pass, db, problemDir);

    public static string InitSchema()
    {
        lock (Lock) return Take(oj_mysql_init_schema());
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
