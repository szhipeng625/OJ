namespace client.DataAccess.Models;

/// <summary>登录/会话校验结果</summary>
public record LoginResult(bool Ok, string Token, long UserId, string Role, string Username, string Error);
