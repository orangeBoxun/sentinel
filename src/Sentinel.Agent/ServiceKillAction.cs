using Sentinel.Core.Abstractions;
using Sentinel.Platform.Actions;

namespace Sentinel.Agent;

/// <summary>
/// Kills the offending process, asking the Service first: a kill issued from the user's own session
/// cannot touch a process running with more privilege than the Agent has. Falls back to killing
/// locally when no Service is running, so the action still works started by hand.
/// </summary>
public sealed class ServiceKillAction : IAction
{
    private readonly ServiceClient _service;
    private readonly KillProcessAction _local = new();

    public ServiceKillAction(ServiceClient service) => _service = service;

    public string Name => "kill-process";

    public bool CanRun(ActionContext context) => _service.IsAvailable || _local.CanRun(context);

    public void Execute(ActionContext context)
    {
        string? serviceError = null;

        if (_service.IsAvailable)
        {
            if (_service.TryKill(context.Window.ProcessId, out serviceError))
                return;
        }

        try
        {
            _local.Execute(context);
        }
        catch (Exception ex)
        {
            // Report both failures: "the service refused and so did the local kill" is a different
            // situation from "the service is not running".
            throw new InvalidOperationException(
                serviceError is null ? ex.Message : $"服务：{serviceError}；本地：{ex.Message}");
        }
    }
}
