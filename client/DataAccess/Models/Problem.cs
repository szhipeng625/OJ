namespace client.DataAccess.Models;

/// <summary>题目（对应 ojcore oj_get_problems 返回的题目元数据）</summary>
public record Problem(int Id, string Title, string Description, string SampleIn, string SampleOut,
                      long TimeLimitMs, long MemLimitMB, string[] Tags, long Version);
