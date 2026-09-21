using System.IO;
using System.Text.Json;
using client.DataAccess.Interop;
using client.DataAccess.Models;

namespace client.DataAccess;

/// <summary>
/// 数据访问层（DAL）：封装对 C++ ojcore.dll 的 P/Invoke 调用，
/// 负责连接初始化、JSON 反序列化，返回强类型模型；不含界面与业务规则。
/// </summary>
public class ApiClient
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool MySqlEnabled { get; private set; }

    public ApiClient()
    {
        // 题目目录：开发环境在 bin/.../net8.0-windows（上溯 4 级到仓库根），
        // 发布环境在 dist/（上溯 1 级）；逐个候选探测，取存在的那个。
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "server", "problems")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "server", "problems")),
            Path.GetFullPath(Path.Combine(baseDir, "server", "problems")),
        };
        string problemDir = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        var dataDir = Path.Combine(baseDir, "ojdata");
        // 先试本地 LSM 模式
        OJInterop.Init(problemDir, dataDir);
        // 再尝试 MySQL 模式（如果同目录有 mysql_config.json 则启用）
        TryInitMysql(problemDir);
    }

    private void TryInitMysql(string problemDir)
    {
        try
        {
            var cfg = Path.Combine(AppContext.BaseDirectory, "mysql_config.json");
            if (!File.Exists(cfg)) { MySqlEnabled = false; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            var root = doc.RootElement;
            string host = root.GetProperty("host").GetString() ?? "localhost";
            int port = root.TryGetProperty("port", out var pp) ? pp.GetInt32() : 3306;
            string user = root.GetProperty("user").GetString() ?? "root";
            string pass = root.GetProperty("pass").GetString() ?? "";
            string db = root.GetProperty("db").GetString() ?? "oj";
            int rc = OJInterop.InitMySQL(host, port, user, pass, db, problemDir);
            MySqlEnabled = (rc == 0);
            if (MySqlEnabled)
            {
                // 首次建表（IF NOT EXISTS，幂等）
                OJInterop.InitMysqlSchemaJson();
            }
        }
        catch { MySqlEnabled = false; }
    }

    // ---------- 题库 / 判题 ----------

    public Task<List<Problem>?> GetProblemsAsync()
        => Task.Run(() => JsonSerializer.Deserialize<List<Problem>>(OJInterop.GetProblemsJson(), Opt));

    public Task<SubmitResult?> SubmitAsync(int problemId, string code)
        => Task.Run(() => JsonSerializer.Deserialize<SubmitResult>(OJInterop.SubmitJson(problemId, code), Opt));

    public Task<SubmitResult?> SubmitExAsync(int problemId, string code, string username, bool virt)
        => Task.Run(() => JsonSerializer.Deserialize<SubmitResult>(
            OJInterop.SubmitExJson(problemId, code, username, virt), Opt));

    // ---------- 比赛 / 榜单 ----------

    public Task<List<ContestInfo>?> GetContestsAsync()
        => Task.Run(() => JsonSerializer.Deserialize<List<ContestInfo>>(OJInterop.GetContestsJson(), Opt));

    public Task<ContestDetail?> GetContestAsync(int cid)
        => Task.Run(() => JsonSerializer.Deserialize<ContestDetail>(OJInterop.GetContestJson(cid), Opt));

    public Task<BoardData?> GetBoardAsync(int cid)
        => Task.Run(() => JsonSerializer.Deserialize<BoardData>(OJInterop.GetBoardJson(cid), Opt));

    /// <summary>比赛提交（contest_id 写入提交记录，供榜单/提交记录按比赛聚合）。</summary>
    public Task<SubmitResult?> SubmitContestAsync(int problemId, string code, string username, bool virt, int contestId)
        => Task.Run(() => JsonSerializer.Deserialize<SubmitResult>(
            OJInterop.SubmitContestJson(problemId, code, username, virt, contestId), Opt));

    /// <summary>报名比赛（幂等）。</summary>
    public Task<ContestRegistration> RegisterContestAsync(int cid, string username, bool virt)
        => Task.Run(() =>
        {
            var j = JsonDocument.Parse(OJInterop.ContestRegisterJson(cid, username, virt)).RootElement;
            bool ok = j.TryGetProperty("ok", out var o) && o.GetBoolean();
            bool v = j.TryGetProperty("virtual", out var vv) && vv.GetBoolean();
            return new ContestRegistration(ok, true, v);
        });

    /// <summary>查询当前用户对某比赛的报名状态。</summary>
    public Task<ContestRegistration> GetContestRegistrationAsync(int cid, string username)
        => Task.Run(() =>
        {
            var j = JsonDocument.Parse(OJInterop.ContestRegistrationJson(cid, username)).RootElement;
            bool registered = j.TryGetProperty("registered", out var r) && r.GetBoolean();
            bool v = j.TryGetProperty("virtual", out var vv) && vv.GetBoolean();
            return new ContestRegistration(true, registered, v);
        });

    /// <summary>比赛提交记录（时间倒序；每人每题保留最后一次结果）。</summary>
    public Task<List<ContestSubmission>?> GetContestSubmissionsAsync(int cid)
        => Task.Run(() => JsonSerializer.Deserialize<List<ContestSubmission>>(
            OJInterop.ContestSubmissionsJson(cid), Opt));

    // ---------- 用户体系 ----------

    public Task<LoginResult?> LoginAsync(string u, string p)
        => Task.Run<LoginResult?>(() =>
        {
            var j = JsonDocument.Parse(OJInterop.LoginJson(u, p)).RootElement;
            return new LoginResult(
                j.GetProperty("ok").GetBoolean(),
                j.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "",
                j.TryGetProperty("userId", out var uid) ? uid.GetInt64() : 0,
                j.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user",
                j.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                j.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
        });

    public Task<bool> RegisterAsync(string u, string p, string role)
        => Task.Run(() =>
        {
            var j = JsonDocument.Parse(OJInterop.RegisterJson(u, p, role)).RootElement;
            return j.GetProperty("ok").GetBoolean();
        });

    public Task<LoginResult?> WhoamiAsync(string token)
        => Task.Run<LoginResult?>(() =>
        {
            var j = JsonDocument.Parse(OJInterop.WhoamiJson(token)).RootElement;
            return new LoginResult(
                j.GetProperty("ok").GetBoolean(),
                token,
                j.TryGetProperty("userId", out var uid) ? uid.GetInt64() : 0,
                j.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user",
                j.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                j.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "");
        });

    public Task LogoutAsync(string token)
        => Task.Run(() => OJInterop.LogoutJson(token));
}
