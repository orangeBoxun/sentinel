using Sentinel.Core.Abstractions;
using Sentinel.Core.Engine;
using Sentinel.Core.Models;

namespace Sentinel.Platform.Actions;

/// <summary>
/// Shows a tray balloon notification. Holds one NotifyIcon for the process lifetime, since
/// recreating it per notification leaks a tray slot each time.
///
/// The Agent has its own tray icon and uses <c>TrayNotifyAction</c> instead, so a second icon does
/// not appear next to it; this one is for console/CLI use.
/// </summary>
public sealed class NotifyAction : IAction, IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public NotifyAction()
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Warning,
            Visible = true,
            Text = "Sentinel",
        };
    }

    public string Name => "notify";

    public bool CanRun(ActionContext context) => true;

    public void Execute(ActionContext context)
    {
        var message = NotifyMessage.Build(context);
        _icon.BalloonTipTitle = message.Title;
        _icon.BalloonTipText = message.Body;
        _icon.BalloonTipIcon = message.Acted
            ? System.Windows.Forms.ToolTipIcon.Error
            : System.Windows.Forms.ToolTipIcon.Warning;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Records the decision to the audit trail (always safe to run).</summary>
public sealed class LogAction : IAction
{
    private readonly IAuditLog _audit;

    public LogAction(IAuditLog audit) => _audit = audit;

    public string Name => "log";

    public bool CanRun(ActionContext context) => true;

    public void Execute(ActionContext context)
        => _audit.Write(new AuditEntry(
            context.Now,
            context.Target.Id,
            context.Verdict.RuleId,
            Name,
            null,
            context.Verdict.Reason));
}
