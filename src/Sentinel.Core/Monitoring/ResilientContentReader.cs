using Sentinel.Core.Abstractions;
using Sentinel.Core.Models;

namespace Sentinel.Core.Monitoring;

/// <summary>
/// 给内容读取加一道超时，让一个读不动的窗口不能把整个监控拖停。
///
/// <para>
/// <b>为什么需要这个：</b>UIA 调用没有超时，也没有取消令牌。目标进程如果不是在泵消息（无响应的
/// 窗口、正在崩溃的进程、卡住的渲染进程），<c>FindAllDescendants</c> 或
/// <c>TextPattern.GetText</c> 就会一直等下去。监控循环是单线程按顺序读每个窗口的，一个窗口卡住
/// 就等于整条循环停在那儿 —— 而它外面看起来毫无异样：进程活着、托盘图标在、界面正常，
/// 但规则已经不生效了。
/// </para>
/// <para>
/// <b>怎么做的：</b>把读取放到一个专职线程上，请求方最多等 <see cref="DefaultTimeout"/>。
/// 超时了就放弃那个线程（它是后台线程，进程退出不受影响），把这个窗口静默一段时间，换一个新的
/// 读取器接着读别的窗口。关键是**只有那一个窗口被跳过**，其余照常。
/// </para>
/// <para>
/// <b>为什么是跳过而不是拦住：</b>读不出来的时候，我们不知道那个窗口里是什么。按"没读到内容"
/// 处理（也就是不动作）和别的读失败路径一致，也就不会因为一个卡住的记事本把整台机器锁死。
/// 但静默是有代价的 —— 它会成为一条绕过路径 —— 所以每次跳过都记下来，见
/// <see cref="LastNotice"/>。
/// </para>
/// </summary>
public sealed class ResilientContentReader : IContentReader, IDisposable
{
    /// <summary>
    /// 一次读取最多等多久。
    ///
    /// 实测一次完整读取（两个筛选查询 + 少量属性读取）约 600 毫秒，所以这个数是它的十几倍 ——
    /// 误判的代价（白白跳过两分钟）比卡住的代价（整个循环停摆）大，宁可放宽。太短的超时会把慢
    /// 机器上正常的读取判成卡死，那等于自己给自己开了个后门。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>第一次跳过时静默多久；每再卡一次翻一倍，上限 16 倍（见 <see cref="MuteFor"/>）。</summary>
    private static readonly TimeSpan BaseMute = TimeSpan.FromMinutes(2);

    private readonly Func<IContentReader> _factory;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();

    private Worker? _worker;
    private readonly Dictionary<string, Mute> _muted = new(StringComparer.Ordinal);

    private sealed record Mute(DateTimeOffset Until, int Strikes);

    public ResilientContentReader(Func<IContentReader> factory, TimeSpan? timeout = null)
    {
        _factory = factory;
        _timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout => _timeout;

    /// <summary>累计有多少次读取超时。诊断用 —— 这个数不为零就说明有窗口读不动。</summary>
    public int TimeoutCount { get; private set; }

    public DateTimeOffset? LastTimeoutAt { get; private set; }

    /// <summary>上一次超时的那个窗口，形如 <c>Codex (PID 1234)</c>。</summary>
    public string? LastTimeoutWindow { get; private set; }

    /// <summary>现在有几个窗口正被跳过。</summary>
    public int MutedCount
    {
        get
        {
            lock (_gate)
                return _muted.Count;
        }
    }

    /// <summary>
    /// 给界面用的一句话：现在有几个窗口读不动、跳过多久。没人读得动的时候是 null。
    /// 跳过是一条潜在的绕过路径，所以它必须能被看见。
    /// </summary>
    public string? LastNotice { get; private set; }

    public bool IsAvailable()
    {
        try
        {
            return Current().Reader.IsAvailable();
        }
        catch
        {
            return false;
        }
    }

    public ReaderCapabilities Capabilities => Current().Reader.Capabilities;

    public ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null)
    {
        var key = Key(window);

        lock (_gate)
        {
            if (_muted.TryGetValue(key, out var mute) && DateTimeOffset.UtcNow < mute.Until)
                return null;

            // 静默期过了，重新给它一次机会。
            _muted.Remove(key);
        }

        if (Current().TryRead(window, options, _timeout, out var snapshot))
            return snapshot;

        Abandon(key, window);
        return null;
    }

    /// <summary>把卡住的那个读取器丢下，换一个新的，并让这个窗口安静一阵子。</summary>
    private void Abandon(string key, WindowRef window)
    {
        lock (_gate)
        {
            // 不 Dispose：那个线程正卡在 UIA 调用里，Dispose 会跟着一起卡住 —— 而躲开这件事正是
            // 这个类的全部意义。它是后台线程，进程退出不受影响；而且如果那次调用以后真回来了，
            // 它自己会收摊（见 Worker.Pump）。
            _worker = null;

            TimeoutCount++;
            LastTimeoutAt = DateTimeOffset.UtcNow;
            LastTimeoutWindow = $"{window.ProcessName} (PID {window.ProcessId})";

            var strikes = _muted.TryGetValue(key, out var previous) ? previous.Strikes + 1 : 1;
            var mute = MuteFor(strikes);
            _muted[key] = new Mute(DateTimeOffset.UtcNow + mute, strikes);

            LastNotice = $"{LastTimeoutWindow} 读不动（UIA 没响应），已跳过 {Describe(mute)}";
        }
    }

    /// <summary>同一个窗口每再卡一次就跳得更久 —— 一个彻底坏掉的窗口不该每分钟都来卡一次读取。</summary>
    private static TimeSpan MuteFor(int strikes) =>
        TimeSpan.FromTicks(BaseMute.Ticks * (1L << Math.Min(strikes - 1, 4)));

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} 分钟" : $"{span.TotalSeconds:0} 秒";

    private static string Key(WindowRef window) => $"{window.ProcessId}:{window.NativeHandle}";

    private Worker Current()
    {
        lock (_gate)
            return _worker ??= new Worker(_factory);
    }

    public void Dispose()
    {
        Worker? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
        }

        worker?.Shutdown();
    }

    /// <summary>
    /// 一个专职读取线程。请求方 <see cref="TryRead"/> 之后就等在这儿，等不到就把它丢下 ——
    /// 丢下的线程会停在 UIA 调用里，所以这里只做成"最多一个请求在飞"，不做队列：
    /// 反正调用方是单线程顺序读的。
    /// </summary>
    private sealed class Worker
    {
        private readonly Thread _thread;
        private readonly AutoResetEvent _request = new(false);
        private readonly AutoResetEvent _done = new(false);
        private readonly ManualResetEventSlim _stop = new(false);

        private WindowRef _window = default!;
        private ReaderOptions? _options;
        private ContentSnapshot? _snapshot;
        private volatile bool _abandoned;

        public Worker(Func<IContentReader> factory)
        {
            Reader = factory();
            _thread = new Thread(Pump) { IsBackground = true, Name = "sentinel-reader" };
            _thread.Start();
        }

        public IContentReader Reader { get; }

        /// <summary>读一次，最多等 <paramref name="timeout"/>。返回 false 表示等超时了。</summary>
        public bool TryRead(WindowRef window, ReaderOptions? options, TimeSpan timeout, out ContentSnapshot? snapshot)
        {
            snapshot = null;

            _window = window;
            _options = options;
            _snapshot = null;

            // 先 Reset 再 Set：上一次的完成信号不能算这一次的。
            _done.Reset();
            _request.Set();

            if (!_done.WaitOne(timeout))
            {
                // 如果那次调用以后真回来了，让它自己退出、自己 Dispose。
                _abandoned = true;
                return false;
            }

            snapshot = _snapshot;
            return true;
        }

        public void Shutdown()
        {
            _abandoned = true;
            _request.Set();

            if (_stop.Wait(TimeSpan.FromSeconds(2)))
                return;

            // 没在时限内收摊，说明它卡在 UIA 里。那就别管它了 —— 后台线程不会拖住进程退出，
            // 而且这正是整个类存在的理由。
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    _request.WaitOne();
                    if (_abandoned)
                        break;

                    try
                    {
                        _snapshot = Reader.Read(_window, _options);
                    }
                    catch
                    {
                        // 读取器自己出错了。当成"没读到"，由调用方按读失败处理。
                        _snapshot = null;
                    }

                    // 读完了才发现已经被放弃：不要再发完成信号（没人等），也别占着资源。
                    if (_abandoned)
                        break;

                    _done.Set();
                }
            }
            finally
            {
                try
                {
                    (Reader as IDisposable)?.Dispose();
                }
                catch
                {
                    // 收摊时出什么错都无关紧要了。
                }

                _stop.Set();
            }
        }
    }
}
