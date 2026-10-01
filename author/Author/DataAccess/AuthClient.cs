using System.IO;
using System.Text.Json;
using author.DataAccess.Interop;
using author.DataAccess.Models;

namespace author.DataAccess;

/// <summary>
/// 认证数据访问：读取 config.xml 的 middlewareUrl，经 ojcore 完成中间件初始化、登录、会话校验。
/// 服务端仅允许 admin / author 角色（角色判定在 BLL AuthService）。
/// </summary>
public sealed class AuthClient
{
    /// <summary>初始化：统一走中间层（MySQL 收口）。false = 未配置或失败，服务端不允许进入。</summary>
    public bool Init(string problemDir, string dataDir)
    {
        try
        {
            // 中间件地址来自 config.xml <middlewareUrl>（替代原 mysql_config.json）
            string mw = AppConfig.Current.MiddlewareUrl.Trim();
            if (string.IsNullOrWhiteSpace(mw)) return false;

            lock (OjCoreInterop.Lock)
                OjCoreInterop.InitMiddleware(mw, problemDir, dataDir);
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
