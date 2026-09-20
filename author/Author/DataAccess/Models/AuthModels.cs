namespace author.DataAccess.Models;

/// <summary>服务端登录会话（与客户端共用 ojcore 的 users/sessions 表）。</summary>
public record Session(bool Ok, string Token, long UserId, string Role, string Username, string Error);
