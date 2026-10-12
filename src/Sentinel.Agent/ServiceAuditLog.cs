using System.Collections.Concurrent;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Logging;

namespace Sentinel.Agent;

/// <summary>
/// 审计一边写两份：本地一份（服务不在也不能丢），服务一份（权威的那份）。
///
/// <para>
/// <b>为什么要给服务一份。</b>Agent 以用户身份运行，所以 <c>logs\</c> 对它必须是可写的；
/// 可写就意味着那份记录**改得动**。而这套东西的全部意义是"约束自己"，凌晨一点会去改的
/// 恰恰是这份记录。服务那份写在只有管理员能写的目录里，普通用户只能读。
/// </para>
/// <para>
/// <b>为什么排队而不是直接发。</b>监控循环是同步的，而一次 IPC 是一个来回 —— 每条审计都等
/// 一次会把它拖慢，而审计只是"顺便留个底"。所以这里入队就返回，一条后台线程慢慢送。
/// </para>
/// </summary>
public sealed class ServiceAuditLog : IAuditLog, IDisposable
{
    /// <summary>攒着还没送出去的条数上限。服务长时间不在时不能把内存吃掉。</summary>
    private const int MaxQueued = 5000;

    private readonly IAuditLog _local;
    private readonly ServiceClient _service;
    private readonly ConcurrentQueue<AuditEntry> _queue = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _sender;

    /// <summary>送失败的那一条。留住它，顺序才不会乱。</summary>
    private AuditEntry? _retry;

    private volatile bool _stop;
    private int _dropped;

    public ServiceAuditLog(IAuditLog local, ServiceClient service)
    {
        _local = local;
        _service = service;

        _sender = new Thread(Pump) { IsBackground = true, Name = "sentinel-audit" };
        _sender.Start();
    }

    public void Write(AuditEntry entry)
    {
        // 本地先写，而且是同步的：这份是"服务不在也不能丢"的那一份。
        _local.Write(entry);

        if (_queue.Count >= MaxQueued)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(entry);
        _wake.Set();
    }

    private void Pump()
    {
        while (!_stop)
        {
            _wake.Wait(TimeSpan.FromSeconds(2));
            _wake.Reset();

            // 丢了多少条要说出来 —— 一个不声不响的缺口，比"记录少了几行"难查得多。
            var missed = Interlocked.Exchange(ref _dropped, 0);

            if (missed > 0)
            {
                _local.Write(new AuditEntry(
                    DateTimeOffset.Now, null, null, "audit-gap", null,
                    $"有 {missed} 条审计因为服务联系不上被丢掉了"));
            }

            // 上一次没送出去的那条先补上，顺序才不会乱。
            if (_retry is { } pending)
            {
                if (!_service.SendAudit(pending))
                    continue;

                _retry = null;
            }

            while (_queue.TryDequeue(out var entry))
            {
                if (_service.SendAudit(entry))
                    continue;

                _retry = entry;
                break;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();

        // 给它一点时间把队尾送出去。送不完也不强求：这是个后台线程，进程退它就没了。
        _sender.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
