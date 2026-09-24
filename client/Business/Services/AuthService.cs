using System.IO;
using client.DataAccess;
using client.DataAccess.Models;

namespace client.Business.Services;

/// <summary>
/// BLL：账号认证、会话恢复与 token 持久化。UI 只依赖本服务，不直接碰 DAL。
/// </summary>
public class AuthService
{
    private readonly ApiClient _api;
    private static readonly string SessionPath =
        Path.Combine(AppContext.BaseDirectory, "ojdata", "session.txt");

    public LoginResult? CurrentUser { get; private set; }

    public AuthService(ApiClient api) => _api = api;

    public Task<LoginResult?> LoginAsync(string username, string password)
        => _api.LoginAsync(username, password);

    /// <summary>注册（仅 user 角色），需邮箱/姓名/学校。</summary>
    public Task<bool> RegisterAsync(string username, string password, string email, string name, string school)
        => _api.RegisterAsync(username, password, email, name, school);

    /// <summary>更新当前用户资料（昵称 / 头像 data URL）。</summary>
    public Task<bool> UpdateProfileAsync(string token, string nickname, string avatar)
        => _api.UpdateProfileAsync(token, nickname, avatar);

    /// <summary>用本地保存的 token 恢复登录态；无效/不存在返回 null。</summary>
    public async Task<LoginResult?> RestoreSessionAsync()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            string token = (await File.ReadAllTextAsync(SessionPath)).Trim();
            if (string.IsNullOrEmpty(token)) return null;
            var me = await _api.WhoamiAsync(token);
            if (me is { Ok: true }) { CurrentUser = me; return me; }
            return null;
        }
        catch { return null; }
    }

    /// <summary>登录成功后保存 token 并记录当前用户。</summary>
    public void SaveSession(LoginResult me)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, me.Token);
        CurrentUser = me;
    }

    /// <summary>退出登录：调用服务端登出并清除本地保存的 token。</summary>
    public async Task LogoutAsync()
    {
        if (CurrentUser is { } me)
        {
            try { await _api.LogoutAsync(me.Token); } catch { /* 服务端登出失败不阻塞本地清理 */ }
        }
        CurrentUser = null;
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { }
    }
}
