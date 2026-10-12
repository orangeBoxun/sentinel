using System.Text.Json;
using Sentinel.Core.Abstractions;

namespace Sentinel.Core.Logging;

public sealed class NullAuditLog : IAuditLog
{
    public void Write(AuditEntry entry)
    {
    }
}

/// <summary>Append-only JSON-lines audit log, opened lazily and flushed per write.</summary>
public sealed class JsonLinesAuditLog : IAuditLog, IDisposable
{
    /// <summary>
    /// 中文按原样写，不转成 <c>\uXXXX</c>。
    ///
    /// <para>
    /// 转义在 JSON 里完全合法，程序读起来也一样；但**这份记录是给人看的**（「日志」页直接把行
    /// 摊出来，出问题的时候也要用记事本打开它）。转义之后一屏全是 <c>规则</c>，
    /// 等于把"能读"这件事丢了。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private StreamWriter? _writer;

    public JsonLinesAuditLog(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public void Write(AuditEntry entry)
    {
        lock (_lock)
        {
            // FileShare.ReadWrite —— 不写这一句就是"只许别人读"，而那样**读的人照样会失败**：
            // 共享是双向的，读的人也得允许写的人继续持有。默认的 StreamWriter(path, append)
            // 就是这么开的，结果是"日志页读不了正在写的日志"。
            _writer ??= new StreamWriter(
                new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));

            _writer.WriteLine(JsonSerializer.Serialize(entry, Json));
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// 读审计文件。
///
/// <para>
/// 单独做成一个东西，是因为"怎么读"这件事有个不显眼的坑：写的人正持有文件时，读的人**也必须
/// 声明允许别人继续写**（共享是双向的）。照着 <c>File.ReadAllLines</c> 那种默认写法去读，
/// 会在"监控正在跑"这个最常见的情况下失败 —— 而那时恰恰是你想看日志的时候。
/// </para>
/// </summary>
public static class AuditFile
{
    /// <summary>读最后 <paramref name="count"/> 行，最新的在最后。</summary>
    public static IReadOnlyList<string> Tail(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var kept = new Queue<string>(count);

        while (reader.ReadLine() is { } line)
        {
            kept.Enqueue(line);

            if (kept.Count > count)
                kept.Dequeue();
        }

        return kept.ToArray();
    }
}

/// <summary>
/// 按天分文件的审计日志：<c>&lt;目录&gt;\audit-20261012.jsonl</c>。
///
/// <para>
/// 服务端（权威那份）和 Agent（本地那份）共用这一个，两边文件名和格式一致 —— 以后要对比
/// 两份记录，不用先搞清"哪边用的什么格式"。
/// </para>
/// <para>
/// 跨天时换文件，判断放在每次写入里：服务是常驻的，没人会替它在新的一天开始时换。
/// </para>
/// </summary>
public sealed class DailyAuditLog : IAuditLog, IDisposable
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly object _lock = new();

    private string? _day;
    private JsonLinesAuditLog? _current;

    public DailyAuditLog(string directory, string prefix = "audit-")
    {
        _directory = directory;
        _prefix = prefix;
    }

    /// <summary>今天这个文件在哪。界面要读它、或者要告诉人"记录在哪"的时候用。</summary>
    public string PathFor(DateTimeOffset when) =>
        System.IO.Path.Combine(_directory, $"{_prefix}{when:yyyyMMdd}.jsonl");

    public void Write(AuditEntry entry)
    {
        lock (_lock)
        {
            var day = entry.Timestamp.ToString("yyyyMMdd");

            if (_day != day)
            {
                _current?.Dispose();
                _current = new JsonLinesAuditLog(PathFor(entry.Timestamp));
                _day = day;
            }

            _current.Write(entry);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _current?.Dispose();
            _current = null;
            _day = null;
        }
    }
}
