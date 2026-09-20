using System.IO;
using System.Text.Json;
using author.DataAccess.Interop;
using author.DataAccess.Models;

namespace author.DataAccess;

/// <summary>
/// 认证数据访问：读取 mysql_config.json，经 ojcore 完成 MySQL 初始化、登录、会话校验。
/// 服务端仅允许 admin / author 角色（角色判定在 BLL AuthService）。
/// </summary>
public sealed class AuthClient
{
    /// <summary>初始化 MySQL 连接并建表。false = 未配置或连接失败，服务端不允许进入。</summary>
    public bool Init(string problemDir)
    {
        try
        {
            string cfgPath = Path.Combine(AppContext.BaseDirectory, "mysql_config.json");
            if (!File.Exists(cfgPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
            var r = doc.RootElement;
            string host = r.TryGetProperty("host", out var h) ? h.GetString() ?? "localhost" : "localhost";
            int port = r.TryGetProperty("port", out var pp) ? pp.GetInt32() : 3306;
            string user = r.TryGetProperty("user", out var u) ? u.GetString() ?? "root" : "root";
            string pass = r.TryGetProperty("pass", out var p) ? p.GetString() ?? "" : "";
            string db = r.TryGetProperty("db", out var d) ? d.GetString() ?? "oj" : "oj";

            int rc;
            // oj_init_mysql 建立进程级单例连接，只在启动时调用一次（不进 OjCoreInterop 的调用锁）
            lock (OjCoreInterop.Lock)
                rc = OjCoreInterop.InitMysql(host, port, user, pass, db, problemDir);
            if (rc != 0) return false;

            try { _ = OjCoreInterop.InitSchema(); } catch { /* 建表失败不阻塞登录 */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Session Login(string u, string p)
    {
        try
        {
            using var j = JsonDocument.Parse(OjCoreInterop.LoginJson(u, p));
            return ParseSession(j.RootElement, "");
        }
        catch (Exception ex) { return new Session(false, "", 0, "", "", ex.Message); }
    }

    public Session Whoami(string token)
    {
        try
        {
            using var j = JsonDocument.Parse(OjCoreInterop.WhoamiJson(token));
            return ParseSession(j.RootElement, token);
        }
        catch (Exception ex) { return new Session(false, "", 0, "", "", ex.Message); }
    }

    private static Session ParseSession(JsonElement j, string token)
    {
        bool ok = j.TryGetProperty("ok", out var o) && o.GetBoolean();
        return new Session(
            ok,
            j.TryGetProperty("token", out var t) ? t.GetString() ?? "" : token,
            j.TryGetProperty("userId", out var uid) ? uid.GetInt64() : 0,
            j.TryGetProperty("role", out var ro) ? ro.GetString() ?? "user" : "user",
            j.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
            j.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
    }
}
