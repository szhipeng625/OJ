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
    /// <summary>初始化：统一走中间层（MySQL 收口）。false = 未配置或失败，服务端不允许进入。</summary>
    public bool Init(string problemDir, string dataDir)
    {
        try
        {
            string cfgPath = Path.Combine(AppContext.BaseDirectory, "mysql_config.json");
            if (!File.Exists(cfgPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
            var r = doc.RootElement;

            // 统一走中间层，middlewareUrl 必填
            string mw = r.TryGetProperty("middlewareUrl", out var mv) ? mv.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(mw)) return false;

            lock (OjCoreInterop.Lock)
                OjCoreInterop.InitMiddleware(mw.Trim(), problemDir, dataDir);
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
