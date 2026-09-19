using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;

namespace author.Services;

/// <summary>
/// 服务端用户认证：P/Invoke ojcore.dll 的 MySQL 登录接口。
/// 与客户端共用同一套用户表。服务端要求 role ∈ {admin, author}。
/// </summary>
public static class UserAuth
{
    private const string Dll = "ojcore.dll";

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

    private static string PtrToUtf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int len = 0; while (Marshal.ReadByte(p, len) != 0) len++;
        var bytes = new byte[len];
        Marshal.Copy(p, bytes, 0, len);
        oj_free_string(p);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public record Session(bool Ok, string Token, long UserId, string Role, string Username, string Error);

    /// <summary>初始化 MySQL 连接。false = 未配置或连接失败，服务端不允许进入。</summary>
    public static bool Init(string problemDir)
    {
        try
        {
            var cfg = Path.Combine(AppContext.BaseDirectory, "mysql_config.json");
            if (!File.Exists(cfg)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            var r = doc.RootElement;
            string host = r.GetProperty("host").GetString() ?? "localhost";
            int port = r.TryGetProperty("port", out var pp) ? pp.GetInt32() : 3306;
            string user = r.GetProperty("user").GetString() ?? "root";
            string pass = r.GetProperty("pass").GetString() ?? "";
            string db = r.GetProperty("db").GetString() ?? "oj";
            int rc = oj_init_mysql(host, port, user, pass, db, problemDir);
            if (rc == 0)
            {
                // 建表（users/sessions/submissions/problem_generators，IF NOT EXISTS 幂等）
                try { PtrToUtf8(oj_mysql_init_schema()); } catch { }
                return true;
            }
            return false;
        }
        catch { return false; }
    }
    public static Session Login(string u, string p)
    {
        try
        {
            var j = JsonDocument.Parse(PtrToUtf8(oj_login(u, p))).RootElement;
            bool ok = j.GetProperty("ok").GetBoolean();
            return new Session(
                ok,
                j.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "",
                j.TryGetProperty("userId", out var uid) ? uid.GetInt64() : 0,
                j.TryGetProperty("role", out var ro) ? ro.GetString() ?? "user" : "user",
                j.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                j.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
        }
        catch (Exception ex) { return new Session(false, "", 0, "", "", ex.Message); }
    }

    public static Session Whoami(string token)
    {
        try
        {
            var j = JsonDocument.Parse(PtrToUtf8(oj_whoami(token))).RootElement;
            bool ok = j.GetProperty("ok").GetBoolean();
            return new Session(
                ok, token,
                j.TryGetProperty("userId", out var uid) ? uid.GetInt64() : 0,
                j.TryGetProperty("role", out var ro) ? ro.GetString() ?? "user" : "user",
                j.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                j.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
        }
        catch (Exception ex) { return new Session(false, "", 0, "", "", ex.Message); }
    }
}
