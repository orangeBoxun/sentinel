using Sentinel.Core.Abstractions;
using Sentinel.Core.Engine;
using Sentinel.Core.Models;

namespace Sentinel.Agent;

/// <summary>
/// Sends notifications through the Agent's own tray icon. Using Platform's NotifyAction here would
/// create a second tray icon alongside the Agent's, which is both confusing and pointless.
/// </summary>
public sealed class TrayNotifyAction : IAction
{
    private readonly Action<string, string, bool, AlertStyle> _show;

    public TrayNotifyAction(Action<string, string, bool, AlertStyle> show) => _show = show;

    public string Name => "notify";

    public bool CanRun(ActionContext context) => true;

    public void Execute(ActionContext context)
    {
        var message = NotifyMessage.Build(context);
        _show(message.Title, message.Body, message.Acted, context.Policy.AlertStyle);
    }
}
