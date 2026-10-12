using System.Diagnostics;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;

namespace Sentinel.Core.Tests;

/// <summary>
/// 读取超时那一层。
///
/// <para>
/// 这些测试针对的是一件很具体的事：一个不响应的窗口。UIA 调用没有超时也没有取消令牌，所以
/// 卡住就是真的卡住 —— 以前它会把整条监控循环拖停，而外面看起来一切正常。
/// </para>
/// </summary>
public sealed class ResilientReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static WindowRef Window(int pid, string name = "app") =>
        new(pid, name, "窗口", @"C:\app.exe", pid * 100);

    /// <summary>卡在读取里，直到测试放它走。</summary>
    private sealed class BlockingReader : IContentReader, IDisposable
    {
        private readonly ManualResetEventSlim _release;
        private readonly int _hangOnPid;

        public BlockingReader(ManualResetEventSlim release, int hangOnPid)
        {
            _release = release;
            _hangOnPid = hangOnPid;
        }

        public bool IsAvailable() => true;
        public ReaderCapabilities Capabilities => new(true, true, true);

        public ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null)
        {
            if (window.ProcessId == _hangOnPid)
                _release.Wait();

            return new ContentSnapshot("https://example.com", window.ProcessName, null, DateTimeOffset.UtcNow);
        }

        public void Dispose() => _release.Set();
    }

    private sealed class ThrowingReader : IContentReader
    {
        public bool IsAvailable() => true;
        public ReaderCapabilities Capabilities => new(true, true, true);
        public ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null) =>
            throw new InvalidOperationException("读取器坏了");
    }

    [Fact]
    public void 读得动就原样返回()
    {
        using var release = new ManualResetEventSlim(false);
        using var reader = new ResilientContentReader(() => new BlockingReader(release, hangOnPid: -1), Timeout);

        var snapshot = reader.Read(Window(1, "Codex"));

        Assert.NotNull(snapshot);
        Assert.Equal("Codex", snapshot.Title);
        Assert.Equal(0, reader.TimeoutCount);
    }

    [Fact]
    public void 卡住的窗口超时后返回空()
    {
        using var release = new ManualResetEventSlim(false);
        using var reader = new ResilientContentReader(() => new BlockingReader(release, hangOnPid: 1), Timeout);

        var watch = Stopwatch.StartNew();
        var snapshot = reader.Read(Window(1, "Codex"));
        watch.Stop();

        Assert.Null(snapshot);
        Assert.Equal(1, reader.TimeoutCount);

        // 它确实等过。
        Assert.True(watch.Elapsed >= Timeout, $"只等了 {watch.ElapsedMilliseconds} 毫秒，没等到超时就返回了。");

        // 这里**故意不设墙钟上界**。曾经写过"必须 5 秒内回来"，结果机器一忙（并行跑 build）
        // 就偶发失败 —— 那是调度的事，不是被测代码的事。
        // "没有无限等"这一条本来就由测试结构保证了：真卡住的话，这一行根本执行不到，
        // 整个测试会挂在那儿直到超时，那才是失败该有的样子。
    }

    [Fact]
    public void 一个窗口卡住不影响别的窗口()
    {
        using var release = new ManualResetEventSlim(false);
        using var reader = new ResilientContentReader(() => new BlockingReader(release, hangOnPid: 1), Timeout);

        Assert.Null(reader.Read(Window(1, "Codex")));

        // 换成别的读取器实例了（卡住的那个被丢下），但读别的窗口照常。
        var other = reader.Read(Window(2, "豆包工作"));

        Assert.NotNull(other);
        Assert.Equal("豆包工作", other.Title);
        Assert.Equal(1, reader.TimeoutCount);
    }

    [Fact]
    public void 卡过的窗口会被跳过而不是每次都等一遍()
    {
        using var release = new ManualResetEventSlim(false);
        using var reader = new ResilientContentReader(() => new BlockingReader(release, hangOnPid: 1), Timeout);

        Assert.Null(reader.Read(Window(1, "Codex")));

        var watch = Stopwatch.StartNew();
        Assert.Null(reader.Read(Window(1, "Codex")));
        watch.Stop();

        // 第二次几乎不用等：不等的话，一个坏窗口每次轮询都能白占十秒。
        Assert.True(watch.Elapsed < Timeout, $"第二次还等了 {watch.ElapsedMilliseconds} 毫秒。");
        Assert.Equal(1, reader.TimeoutCount);
        Assert.Equal(1, reader.MutedCount);
    }

    [Fact]
    public void 跳过这件事会说出来()
    {
        using var release = new ManualResetEventSlim(false);
        using var reader = new ResilientContentReader(() => new BlockingReader(release, hangOnPid: 1), Timeout);

        reader.Read(Window(1, "Codex"));

        // 跳过窗口是一条绕过路径 —— 它必须看得见，不能悄悄发生。
        Assert.NotNull(reader.LastNotice);
        Assert.Contains("Codex", reader.LastNotice);
        Assert.NotNull(reader.LastTimeoutAt);
        Assert.Equal("Codex (PID 1)", reader.LastTimeoutWindow);
    }

    [Fact]
    public void 读取器自己抛异常不算超时()
    {
        using var reader = new ResilientContentReader(() => new ThrowingReader(), Timeout);

        Assert.Null(reader.Read(Window(1, "Codex")));

        // 那是"没读到内容"，不是"卡住了"。混在一起的话，一次读失败会连累之后两分钟都读不了。
        Assert.Equal(0, reader.TimeoutCount);
        Assert.Equal(0, reader.MutedCount);
    }
}
