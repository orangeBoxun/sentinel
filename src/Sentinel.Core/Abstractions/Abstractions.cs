using Sentinel.Core.Models;

namespace Sentinel.Core.Abstractions;

public sealed record ReaderCapabilities(bool CanReadUrl, bool CanReadTitle, bool CanReadBody);

/// <summary>Reads title/URL/body from a window. Implementations hide the UIA/OCR backend.</summary>
public interface IContentReader
{
    bool IsAvailable();
    ReaderCapabilities Capabilities { get; }

    /// <summary>
    /// Reads the window. <paramref name="options"/> overrides the reader's own defaults so the
    /// monitor loop can take a cheap URL+title probe before paying for a full body read.
    /// </summary>
    ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null);
}

/// <summary>Enumerates top-level windows across the session.</summary>
public interface IWindowEnumerator
{
    IReadOnlyList<WindowRef> Enumerate();
}

/// <summary>The result of judging a snapshot.</summary>
public sealed record Verdict
{
    public bool IsBlocked { get; init; }
    public string? RuleId { get; init; }
    public string? Reason { get; init; }
    public string? PolicyId { get; init; }

    public static Verdict Allow() => new() { IsBlocked = false };

    public static Verdict Block(string ruleId, string reason, string policyId) => new()
    {
        IsBlocked = true,
        RuleId = ruleId,
        Reason = reason,
        PolicyId = policyId,
    };
}

public sealed record JudgeContext(Target Target, ContentSnapshot Snapshot, DateTimeOffset Now);

/// <summary>Decides Allow/Block for a snapshot. v1 = keyword/regex/whitelist + schedule.</summary>
public interface IJudge
{
    string Name { get; }
    Verdict Judge(JudgeContext context);
}

public sealed record ActionContext(
    WindowRef Window,
    Target Target,
    ContentSnapshot Snapshot,
    Policy Policy,
    Verdict Verdict,
    DateTimeOffset Now,
    ActionStage Stage,
    int HitNumber = 0,
    string? ExecutedSummary = null);

/// <summary>A single policy action (notify/minimize/close/kill/...).</summary>
public interface IAction
{
    string Name { get; }
    bool CanRun(ActionContext context);
    void Execute(ActionContext context);
}

/// <summary>Strong-lock protection. v1 = NoopProtection + config bits only.</summary>
public interface IProtection
{
    bool IsLocked();
    bool VerifyAccess(string secret);
    void BeginLock(object spec);
    void GuardServiceConfig();
    void GuardAutoStart();
    void GuardUninstall();
    void WatchdogTick();
}

/// <summary>
/// One line of the audit trail. <paramref name="Notice"/> records what the user was told, when a
/// notification was shown — otherwise there is no way to tell "nothing happened" from "it told you
/// and you missed it".
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string? TargetId,
    string? RuleId,
    string? Action,
    string? UrlHash,
    string? Reason,
    string? Notice = null);

public interface IAuditLog
{
    void Write(AuditEntry entry);
}
