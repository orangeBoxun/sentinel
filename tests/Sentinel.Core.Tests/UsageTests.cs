using System.IO;
using Sentinel.Core.Model;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 用量统计：请求数、失败数、token，以及"服务商没回 usage"那部分。
/// </summary>
public sealed class UsageTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "sentinel-usage-" + Guid.NewGuid().ToString("N"), "usage.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, true);
        }
        catch
        {
            // 临时目录清不掉不值得让测试失败。
        }
    }

    [Fact]
    public void 记录请求_失败和token分开算()
    {
        var store = new UsageStore(_path);

        store.Record("glm", 1200, 80, answered: true);
        store.Record("glm", null, null, answered: false);   // 超时那次：没有 usage

        var today = store.Since(1)["glm"];

        Assert.Equal(2, today.Requests);
        Assert.Equal(1, today.Failures);
        Assert.Equal(1200, today.PromptTokens);
        Assert.Equal(80, today.CompletionTokens);
        Assert.Equal(1, today.Reported);   // 只有一次真的带了 usage
    }

    [Fact]
    public void 换一个实例读同一个文件_数字还在()
    {
        var first = new UsageStore(_path);
        first.Record("a", 10, 5, answered: true);
        first.Record("b", 20, 7, answered: true);

        var second = new UsageStore(_path);
        var week = second.Since(7);

        Assert.Equal(10, week["a"].PromptTokens);
        Assert.Equal(27, week["b"].PromptTokens + week["b"].CompletionTokens);
    }

    [Fact]
    public void 不同服务商分开记()
    {
        var store = new UsageStore(_path);

        store.Record("cheap", 100, 10, answered: true);
        store.Record("expensive", 9000, 300, answered: true);
        store.Record("expensive", 8000, 250, answered: true);

        var today = store.Since(1);

        Assert.Equal(1, today["cheap"].Requests);
        Assert.Equal(2, today["expensive"].Requests);
        Assert.Equal(110, today["cheap"].PromptTokens + today["cheap"].CompletionTokens);
        Assert.Equal(17550, today["expensive"].PromptTokens + today["expensive"].CompletionTokens);
    }

    [Fact]
    public void 按天摊开_没有记录的那几天也占一格()
    {
        var store = new UsageStore(_path);
        store.Record("a", 10, 5, answered: true);

        var daily = store.Daily(7);

        Assert.Equal(7, daily.Count);
        Assert.NotEmpty(daily[^1].Providers);            // 最后一天是今天，有数据
        Assert.All(daily.Take(6), d => Assert.Empty(d.Providers));
    }

    [Fact]
    public void 老版本按天存的记录_归到当天零点_两边数字对得上()
    {
        // 用户报的："柱状图 2 次、表格 6 次" —— 4 次来自上一版按天存的桶，按小时的图不认识。
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path,
            "{\"2026-10-25\":{\"a\":{\"Requests\":4,\"Failures\":0,"
            + "\"PromptTokens\":40,\"CompletionTokens\":8,\"Reported\":4}}}");

        var store = new UsageStore(_path);

        // 按天的汇总读得到
        Assert.Equal(4, store.Since(1).GetValueOrDefault("a")!.Requests);

        // 按小时的那张图也读得到（落在 00:00 那一格），不会"图上少一截"
        var hourly = store.Hourly();
        Assert.Equal("00:00", hourly[0].Slot);
        Assert.Equal(4, hourly[0].Providers["a"].Requests);
    }

    [Fact]
    public void 文件坏了就当没有_不影响监控()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        var store = new UsageStore(_path);

        Assert.Empty(store.Since(1));

        // 还能继续记
        store.Record("a", 1, 2, answered: true);
        Assert.Equal(1, new UsageStore(_path).Since(1)["a"].Requests);
    }
}
