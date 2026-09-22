using System.IO;
using author.DataAccess;
using author.DataAccess.Models;

namespace author.Business.Services;

/// <summary>认证业务：MySQL 初始化、登录与角色门禁、会话恢复与 token 持久化。</summary>
public sealed class AuthService
{
    private readonly AuthClient _client;

    public AuthService(AuthClient client) => _client = client;

    public Session? CurrentUser { get; private set; }

    /// <summary>初始化 MySQL（服务端必须可用）。</summary>
    public Task<bool> InitAsync(string problemDir, string dataDir) => Task.Run(() => _client.Init(problemDir, dataDir));

    /// <summary>登录并校验角色（仅 admin/author）。</summary>
    public Task<Session> LoginAsync(string username, string password)
        => Task.Run(() =>
        {
            var s = _client.Login(username, password);
            if (s.Ok && s.Role is not ("admin" or "author"))
                return s with { Ok = false, Error = "当前账号角色为 " + s.Role + "，出题端仅允许 admin/author 登录" };
            return s;
        });

    /// <summary>用已保存的 token 恢复会话；角色不符或失效返回 null。</summary>
    public Task<Session?> RestoreSessionAsync(string tokenPath)
        => Task.Run<Session?>(() =>
        {
            try
            {
                if (!File.Exists(tokenPath)) return null;
                string token = File.ReadAllText(tokenPath).Trim();
                if (string.IsNullOrEmpty(token)) return null;
                var me = _client.Whoami(token);
                if (me.Ok && me.Role is "admin" or "author")
                {
                    CurrentUser = me;
                    return me;
                }
                return null;
            }
            catch { return null; }
        });

    public void SetCurrent(Session s) => CurrentUser = s;

    public void SaveSession(string tokenPath, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        File.WriteAllText(tokenPath, token);
    }
}
