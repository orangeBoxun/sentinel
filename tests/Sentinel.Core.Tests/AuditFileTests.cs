using Sentinel.Core.Abstractions;
using Sentinel.Core.Logging;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 审计文件的读写。
///
/// <para>
/// 这里两条都是**真出过的事**，而且都属于"不报错、只是内容不对或者读不出来"那类。
/// </para>
/// </summary>
public sealed class AuditFileTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"sentinel-audit-test-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void 写得正欢的时候也读得到()
    {
        // 真 bug：写入端默认只允许别人读，而**共享是双向的** —— 读的人也得声明允许写的人继续持有。
        // 结果是"只要监控写过一条日志，「日志」页就读不了它"，而那恰恰是你想看日志的时候。
        var path = TempFile();

        try
        {
            using var writer = new JsonLinesAuditLog(path);
            writer.Write(new AuditEntry(DateTimeOffset.Now, "t1", "r1", "close", null, "写一条"));

            // 以前这一行抛 IOException（文件正被占用）。
            var lines = AuditFile.Tail(path, 10);

            Assert.Single(lines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 中文按原样写不转义()
    {
        // 审计是给人看的（「日志」页直接把行摊出来，出问题还要用记事本打开）。
        // 转义之后一屏全是 规则，等于把"能读"这件事丢了。
        var path = TempFile();

        try
        {
            using (var writer = new JsonLinesAuditLog(path))
                writer.Write(new AuditEntry(DateTimeOffset.Now, "t1", "r1", "close", null, "规则命中"));

            var line = AuditFile.Tail(path, 1)[0];

            Assert.Contains("规则命中", line);
            Assert.DoesNotContain("\\u", line);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 只留最后几行_最新的在最后()
    {
        var path = TempFile();

        try
        {
            using (var writer = new JsonLinesAuditLog(path))
            {
                for (var i = 1; i <= 10; i++)
                    writer.Write(new AuditEntry(DateTimeOffset.Now, "t1", "r1", "close", null, $"第 {i} 条"));
            }

            var tail = AuditFile.Tail(path, 3);

            Assert.Equal(3, tail.Count);
            Assert.Contains("第 10 条", tail[^1]);
            Assert.Contains("第 8 条", tail[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 按天分文件()
    {
        // 服务是常驻的，没人会替它在新的一天开始时换文件 —— 判断放在每次写入里。
        var dir = Path.Combine(Path.GetTempPath(), $"sentinel-audit-dir-{Guid.NewGuid():N}");

        try
        {
            using var log = new DailyAuditLog(dir);

            log.Write(new AuditEntry(new DateTimeOffset(2026, 10, 12, 23, 59, 0, TimeSpan.Zero), "t", null, null, null, "昨天"));
            log.Write(new AuditEntry(new DateTimeOffset(2026, 10, 13, 0, 1, 0, TimeSpan.Zero), "t", null, null, null, "今天"));

            Assert.True(File.Exists(Path.Combine(dir, "audit-20261012.jsonl")));
            Assert.True(File.Exists(Path.Combine(dir, "audit-20261013.jsonl")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
