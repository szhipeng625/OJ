using System.IO;
using System.Text.Json;

namespace client.Services;

public record Problem(int Id, string Title, string Description, string SampleIn, string SampleOut,
                      long TimeLimitMs, long MemLimitMB, string[] Tags, long Version);

public record CaseResult(string Name, int TimeMs, bool Passed, string Info);

public record SubmitResult(long Id, string Verdict, string Detail, List<CaseResult> Cases);

public record ContestInfo(int Id, string Name, int ProblemCount, string StartTime, string EndTime);

public record ContestDetail(int Id, string Name, string Description, string StartTime, string EndTime, int[] Problems);

public record BoardRow(int Rank, string Username, int Solved, long Penalty);

public record BoardData(List<BoardRow> Official, List<BoardRow> Virtual);

public record LoginResult(bool Ok, string Token, long UserId, string Role, string Username, string Error);

/// <summary>
/// 与 C++ 判题核心 ojcore.dll 通信（P/Invoke），返回强类型对象。
/// 注意：题目历史版本仅服务端（author）可见，客户端不提供查看接口。
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

    public Task<List<Problem>?> GetProblemsAsync()
        => Task.Run(() => JsonSerializer.Deserialize<List<Problem>>(OJInterop.GetProblemsJson(), Opt));

    public Task<SubmitResult?> SubmitAsync(int problemId, string code)
        => Task.Run(() => JsonSerializer.Deserialize<SubmitResult>(OJInterop.SubmitJson(problemId, code), Opt));

    public Task<SubmitResult?> SubmitExAsync(int problemId, string code, string username, bool virt)
        => Task.Run(() => JsonSerializer.Deserialize<SubmitResult>(
            OJInterop.SubmitExJson(problemId, code, username, virt), Opt));

    public Task<List<ContestInfo>?> GetContestsAsync()
        => Task.Run(() => JsonSerializer.Deserialize<List<ContestInfo>>(OJInterop.GetContestsJson(), Opt));

    public Task<ContestDetail?> GetContestAsync(int cid)
        => Task.Run(() => JsonSerializer.Deserialize<ContestDetail>(OJInterop.GetContestJson(cid), Opt));

    public Task<BoardData?> GetBoardAsync(int cid)
        => Task.Run(() => JsonSerializer.Deserialize<BoardData>(OJInterop.GetBoardJson(cid), Opt));

    public Task<LoginResult?> LoginAsync(string u, string p)
        => Task.Run(() =>
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
        => Task.Run(() =>
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
