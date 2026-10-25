using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Core.Model;

/// <summary>一个服务商在某一天（或某个区间）的用量。</summary>
public sealed class UsageCounters
{
    /// <summary>发出去的判定请求数（成功失败都算）。</summary>
    public int Requests { get; set; }

    /// <summary>没拿到判定的次数（超时、连不上、5xx、格式看不懂）。</summary>
    public int Failures { get; set; }

    /// <summary>输入 token。服务商没回 usage 的请求不计入。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>有多少次请求真的带了 usage —— 没带的话上面两个数是偏小的。</summary>
    public int Reported { get; set; }

    /// <summary>命中缓存的输入 token。服务商不报的请求不计入。</summary>
    public long CachedTokens { get; set; }

    /// <summary>有多少次请求带了缓存命中信息。</summary>
    public int CacheReported { get; set; }
}

/// <summary>
/// 每个服务商每天用了多少 —— 请求数、失败数、token。
///
/// <para>
/// 为什么要有它：判断链是"便宜的先问"省钱的，但省了多少、钱花在谁身上，界面上得看得见。
/// 只记**服务商自己回的 usage**（OpenAI 那套的 prompt/completion，Anthropic 的 input/output）；
/// 有的中转站根本不回 usage，那部分只能记请求数，界面上要说清楚。
/// </para>
/// <para>
/// 存在日志目录里（不是配置目录）：那是用户可写的地方，这份数据也只是给人看的，
/// 不参与任何判断。按天分桶，超过 <see cref="KeepDays"/> 天的自动丢掉。
/// </para>
/// </summary>
public sealed class UsageStore
{
    public const string FileName = "usage.json";

    /// <summary>留多少天。按天分桶，一年下来也就几百 KB。</summary>
    private const int KeepDays = 120;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string _path;

    private Dictionary<string, Dictionary<string, UsageCounters>> _days;

    public UsageStore(string path)
    {
        _path = path;
        _days = Load(path);
    }

    /// <summary>记一次调用。没拿到判定的那次也记 —— 失败次数本身就是有用的信息。</summary>
    public void Record(string providerId, int? promptTokens, int? completionTokens, bool answered,
                       int? cachedTokens = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        // 按**小时**分桶：日/周的统计从小时桶里聚合出来。这样「今天」能按时段看，
        // 而按天/按月的视图一点不受影响。
        var day = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH");

        lock (_gate)
        {
            if (!_days.TryGetValue(day, out var providers))
            {
                providers = new Dictionary<string, UsageCounters>(StringComparer.Ordinal);
                _days[day] = providers;
            }

            if (!providers.TryGetValue(providerId, out var counters))
            {
                counters = new UsageCounters();
                providers[providerId] = counters;
            }

            counters.Requests++;

            if (!answered)
                counters.Failures++;

            if (promptTokens is { } prompt && completionTokens is { } completion)
            {
                counters.PromptTokens += prompt;
                counters.CompletionTokens += completion;
                counters.Reported++;
            }

            if (cachedTokens is { } cached)
            {
                counters.CachedTokens += cached;
                counters.CacheReported++;
            }

            Prune();
            Save();
        }
    }

    /// <summary>取最近几天（含今天）的合计，按服务商分组。</summary>
    public IReadOnlyDictionary<string, UsageCounters> Since(int days)
    {
        var today = DateTimeOffset.Now.Date;
        var from = today.AddDays(-(days - 1)).ToString("yyyy-MM-dd");

        var total = new Dictionary<string, UsageCounters>(StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var (bucket, providers) in _days)
            {
                if (string.CompareOrdinal(DayOf(bucket), from) < 0)
                    continue;

                foreach (var (id, counters) in providers)
                {
                    if (!total.TryGetValue(id, out var sum))
                    {
                        sum = new UsageCounters();
                        total[id] = sum;
                    }

                    sum.Requests += counters.Requests;
                    sum.Failures += counters.Failures;
                    sum.PromptTokens += counters.PromptTokens;
                    sum.CompletionTokens += counters.CompletionTokens;
                    sum.Reported += counters.Reported;
                    sum.CachedTokens += counters.CachedTokens;
                    sum.CacheReported += counters.CacheReported;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// 按天摊开（含今天，往前往后补齐没有记录的那几天，值是空的）。
    ///
    /// <para>
    /// 折线图要的是"每天一个点"，缺一天不能把曲线连歪，所以空的那天补一个空的统计对象。
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Day, IReadOnlyDictionary<string, UsageCounters> Providers)> Daily(int days)
    {
        var today = DateTimeOffset.Now.Date;
        var result = new List<(string, IReadOnlyDictionary<string, UsageCounters>)>();

        lock (_gate)
        {
            for (var offset = days - 1; offset >= 0; offset--)
            {
                var day = today.AddDays(-offset).ToString("yyyy-MM-dd");
                result.Add((day, BucketsOf(day)));
            }
        }

        return result;
    }

    /// <summary>
    /// 按小时摊开（今天 00:00 到现在）——「今天」那张图按时段看。
    ///
    /// <para>
    /// 老版本的记录是按天存的（键里没有小时），它们没法摊到具体时段，只能出现在按天的视图里。
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Slot, IReadOnlyDictionary<string, UsageCounters> Providers)> Hourly()
    {
        var now = DateTimeOffset.Now;
        var result = new List<(string, IReadOnlyDictionary<string, UsageCounters>)>();

        lock (_gate)
        {
            for (var hour = 0; hour <= now.Hour; hour++)
            {
                var stamp = now.ToString("yyyy-MM-dd") + "T" + hour.ToString("00");
                result.Add(($"{hour:00}:00", BucketsOf(stamp)));
            }
        }

        return result;
    }

    /// <summary>把 key 以某天/某小时开头的桶合并起来。</summary>
    private Dictionary<string, UsageCounters> BucketsOf(string prefix)
    {
        var merged = new Dictionary<string, UsageCounters>(StringComparer.Ordinal);

        foreach (var (bucket, providers) in _days)
        {
            if (!bucket.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            foreach (var (id, counters) in providers)
            {
                if (!merged.TryGetValue(id, out var sum))
                {
                    sum = new UsageCounters();
                    merged[id] = sum;
                }

                sum.Requests += counters.Requests;
                sum.Failures += counters.Failures;
                sum.PromptTokens += counters.PromptTokens;
                sum.CompletionTokens += counters.CompletionTokens;
                sum.Reported += counters.Reported;
                sum.CachedTokens += counters.CachedTokens;
                sum.CacheReported += counters.CacheReported;
            }
        }

        return merged;
    }

    /// <summary>桶键的日期部分（老数据是 "yyyy-MM-dd"，新数据是 "yyyy-MM-ddTHH"）。</summary>
    private static string DayOf(string bucket) => bucket.Length >= 10 ? bucket[..10] : bucket;

    private void Prune()
    {
        var oldest = DateTimeOffset.Now.Date.AddDays(-KeepDays).ToString("yyyy-MM-dd");

        foreach (var bucket in _days.Keys.Where(k => string.CompareOrdinal(DayOf(k), oldest) < 0).ToList())
            _days.Remove(bucket);
    }

    private static Dictionary<string, Dictionary<string, UsageCounters>> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, Dictionary<string, UsageCounters>>(StringComparer.Ordinal);

            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, UsageCounters>>>(File.ReadAllText(path), Options)
                   ?? new Dictionary<string, Dictionary<string, UsageCounters>>(StringComparer.Ordinal);
        }
        catch
        {
            // 读坏了就当没有 —— 这只是统计，不该影响监控。
            return new Dictionary<string, Dictionary<string, UsageCounters>>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(_days, Options));
        }
        catch
        {
            // 写不进去就少一次记录，不影响判定。
        }
    }
}
