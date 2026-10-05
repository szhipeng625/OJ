using System.Text;

namespace OjCodex;

/// <summary>
/// 稀疏向量：term → weight。用字典而非定长数组 —— 题库文档短、词表稀疏，
/// 稀疏表示既省内存又让余弦退化成一次点积（向量已 L2 归一化）。
/// </summary>
public sealed class SparseVector
{
    public IReadOnlyDictionary<string, double> Weights { get; }

    public SparseVector(Dictionary<string, double> weights) => Weights = weights;

    public static readonly SparseVector Empty = new(new Dictionary<string, double>());

    /// <summary>余弦相似度（两侧都已 L2 归一化，点积即余弦）。遍历较短的一侧。</summary>
    public double Cosine(SparseVector other)
    {
        if (ReferenceEquals(this, other)) return 1.0;
        var (small, big) = Weights.Count <= other.Weights.Count
            ? (Weights, other.Weights)
            : (other.Weights, Weights);

        double dot = 0;
        foreach (var (term, w) in small)
            if (big.TryGetValue(term, out var v))
                dot += w * v;
        return dot;
    }
}

/// <summary>
/// 分词：中英混排都能用。
///   · CJK：单字 + 相邻双字（双字能抓到「动态规划」这类词，单字保证短查询也能命中）
///   · 拉丁/数字：连续 [a-z0-9_]+，长度 ≥ 2
/// 没有外部依赖、结果确定 —— 同一个题库每次构建出完全一样的索引，缓存才敢按内容哈希。
/// </summary>
public static class CodexTokenizer
{
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var latin = new StringBuilder();
        char prevCjk = '\0';

        void FlushLatin()
        {
            if (latin.Length >= 2) tokens.Add(latin.ToString());
            latin.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushLatin();
                tokens.Add(ch.ToString());
                if (prevCjk != '\0') tokens.Add(string.Concat(prevCjk, ch));
                prevCjk = ch;
            }
            else if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                prevCjk = '\0';
                latin.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                FlushLatin();
                prevCjk = '\0';
            }
        }
        FlushLatin();
        return tokens;
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||   // 基本汉字
        (c >= 0x3400 && c <= 0x4DBF);     // 扩展 A
}

/// <summary>
/// 词袋 → TF-IDF 稀疏向量。IDF 来自本次语料，TF 用 sublinear（1+ln tf）抑制长文档。
/// </summary>
public sealed class CodexVectorizer
{
    private readonly Dictionary<string, double> _idf = new(StringComparer.Ordinal);
    private readonly double _defaultIdf;

    public CodexVectorizer(IEnumerable<string> documents)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        int docCount = 0;

        foreach (var doc in documents)
        {
            docCount++;
            foreach (var term in CodexTokenizer.Tokenize(doc).Distinct(StringComparer.Ordinal))
                df[term] = df.GetValueOrDefault(term) + 1;
        }

        // 平滑 IDF：ln((N+1)/(df+1)) + 1，避免单文档语料下全为 0
        foreach (var (term, d) in df)
            _idf[term] = Math.Log((docCount + 1.0) / (d + 1.0)) + 1.0;

        _defaultIdf = Math.Log(docCount + 1.0) + 1.0;
    }

    public SparseVector Vectorize(string? text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in CodexTokenizer.Tokenize(text))
            counts[t] = counts.GetValueOrDefault(t) + 1;
        if (counts.Count == 0) return SparseVector.Empty;

        var weights = new Dictionary<string, double>(counts.Count, StringComparer.Ordinal);
        double norm = 0;
        foreach (var (term, tf) in counts)
        {
            double w = (1.0 + Math.Log(tf)) * _idf.GetValueOrDefault(term, _defaultIdf);
            weights[term] = w;
            norm += w * w;
        }

        norm = Math.Sqrt(norm);
        if (norm <= 0) return SparseVector.Empty;
        foreach (var term in weights.Keys.ToList())
            weights[term] /= norm;

        return new SparseVector(weights);
    }
}

/// <summary>检索命中的一条。</summary>
public sealed record RetrievalHit<T>(T Item, double Score);

/// <summary>
/// 向量索引 + 两级缓存。
///
/// 缓存分两层，各自解决一个开销：
///   · 索引缓存：按「题库内容哈希」缓存向量与 IDF。题库没变就不重建（重建要遍历全部文档分词）。
///   · 查询缓存：按归一化后的查询串缓存 top-K 结果。同一句指令重复问（改完再看一遍、重试）
///     直接命中，不再算相似度。
/// 查询缓存带容量上限并按插入顺序淘汰，避免长时间运行后无限增长。
/// </summary>
public sealed class CodexVectorIndex<T>
{
    private readonly Func<T, string> _textOf;
    private readonly Dictionary<string, List<RetrievalHit<T>>> _queryCache = new(StringComparer.Ordinal);
    private readonly int _queryCacheCapacity;
    private readonly Queue<string> _queryCacheOrder = new();

    private string _indexedHash = "";
    private List<T> _items = [];
    private List<SparseVector> _vectors = [];
    private CodexVectorizer? _vectorizer;

    public CodexVectorIndex(Func<T, string> textOf, int queryCacheCapacity = 64)
    {
        _textOf = textOf;
        _queryCacheCapacity = Math.Max(4, queryCacheCapacity);
    }

    /// <summary>索引当前条数（0 表示还没构建）。</summary>
    public int IndexedCount => _items.Count;

    /// <summary>按内容哈希判断要不要重建；题库变了才重算。</summary>
    public void EnsureBuilt(IReadOnlyList<T> items)
    {
        string hash = ComputeHash(items);
        if (hash == _indexedHash && _items.Count == items.Count) return;

        var texts = items.Select(_textOf).ToList();
        _vectorizer = new CodexVectorizer(texts);
        _items = items.ToList();
        _vectors = texts.Select(t => _vectorizer.Vectorize(t)).ToList();
        _indexedHash = hash;
        _queryCache.Clear();
        _queryCacheOrder.Clear();
    }

    /// <summary>检索 top-K。题库不超过 K 时直接全量返回（检索没有意义，别白算）。</summary>
    public IReadOnlyList<RetrievalHit<T>> Search(string query, int topK)
    {
        if (_items.Count == 0) return [];
        topK = Math.Max(1, topK);
        if (_items.Count <= topK)
            return _items.Select(i => new RetrievalHit<T>(i, 1.0)).ToList();

        string key = NormalizeQuery(query);
        if (_queryCache.TryGetValue(key, out var cached)) return cached;

        var qv = _vectorizer!.Vectorize(query);
        var hits = _items
            .Select((item, i) => new RetrievalHit<T>(item, _vectors[i].Cosine(qv)))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

        // 一个都没命中（查询里的词题库里完全没有）时不缓存，让改了指令后能重新算
        if (hits.Count > 0) CacheQuery(key, hits);
        return hits;
    }

    private void CacheQuery(string key, List<RetrievalHit<T>> hits)
    {
        if (_queryCache.Count >= _queryCacheCapacity && _queryCacheOrder.Count > 0)
            _queryCache.Remove(_queryCacheOrder.Dequeue());

        _queryCache[key] = hits;
        _queryCacheOrder.Enqueue(key);
    }

    private static string NormalizeQuery(string query)
    {
        var sb = new StringBuilder(query.Length);
        foreach (var ch in query.Trim())
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>内容哈希：id + 文本的确定性摘要，用来判断题库是否变化。</summary>
    private string ComputeHash(IReadOnlyList<T> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append(_textOf(item));
            sb.Append('\u0001');
        }
        unchecked
        {
            // FNV-1a：够稳、够快，且不占加密库依赖
            ulong h = 1469598103934665603UL;
            foreach (var ch in sb.ToString())
            {
                h ^= ch;
                h *= 1099511628211UL;
            }
            return $"{h:x16}:{items.Count}";
        }
    }
}
