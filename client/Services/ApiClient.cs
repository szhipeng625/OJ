using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace client.Services;

public record Problem(int Id, string Title, string Description, string SampleIn, string SampleOut);

public record CaseResult(string Name, int TimeMs, bool Passed, string Info);

public record SubmitResult(long Id, string Verdict, string Detail, List<CaseResult> Cases);

/// <summary>
/// 与 OJ 后端（Go 服务）通信的客户端。
/// </summary>
public class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(string baseAddress = "http://localhost:8080/")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress) };
    }

    public async Task<List<Problem>?> GetProblemsAsync()
    {
        return await _http.GetFromJsonAsync<List<Problem>>("api/problems");
    }

    public async Task<SubmitResult?> SubmitAsync(int problemId, string code)
    {
        var resp = await _http.PostAsJsonAsync("api/submit", new { problemId, code });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SubmitResult>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
