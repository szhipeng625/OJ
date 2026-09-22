namespace client.DataAccess.Models;

/// <summary>题目（对应 ojcore oj_get_problems 返回的题目元数据）</summary>
public record Problem(int Id, string Title, string Description, string SampleIn, string SampleOut,
                      long TimeLimitMs, long MemLimitMB, string[] Tags,
                      List<TestCaseSample>? Samples = null);

/// <summary>一道题上传到数据库的测试样例（丰富题面/调试用）</summary>
public record TestCaseSample(string Name, string Input, string Output, bool IsSample);
