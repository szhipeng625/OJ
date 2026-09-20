using System.IO;

namespace client.Business.Services;

/// <summary>
/// BLL：未启用 MySQL 时的本地昵称（榜单署名）持久化。
/// 仅负责读写；昵称输入弹窗属于 UI，由 View 处理。
/// </summary>
public class LocalIdentityService
{
    private readonly string _path;

    public LocalIdentityService()
    {
        _path = Path.Combine(AppContext.BaseDirectory, "ojdata", "nickname.txt");
    }

    /// <summary>是否已保存过昵称（用于决定是否首次弹窗询问）。</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>读取昵称；不存在或为空时返回 "anonymous"。</summary>
    public string Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string n = File.ReadAllText(_path).Trim();
                if (n.Length > 0) return n;
            }
        }
        catch { /* 读取失败用默认值 */ }
        return "anonymous";
    }

    public void Save(string nickname)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, nickname);
        }
        catch { /* 昵称持久化失败不影响主流程 */ }
    }
}
