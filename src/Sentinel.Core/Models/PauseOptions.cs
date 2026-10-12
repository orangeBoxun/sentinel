namespace Sentinel.Core.Models;

/// <summary>
/// The bounded exception, per target: this one may be set aside for a few minutes without anyone's
/// permission, once every so often.
///
/// <para>
/// Every other way of switching monitoring off is a weakening, and weakening needs the token. This
/// one exists because the alternative to a bounded pause is not "the user stays focused" — it is
/// the user reaching for the token, or for the uninstaller. A concession with a budget spends the
/// urge without spending the lock.
/// </para>
/// <para>
/// It belongs on the target rather than on the whole program because that is the shape of the urge:
/// one app, one evening. A global allowance would be spent on whichever target was reached for
/// first and then leave the others with nothing.
/// </para>
/// <para>
/// Which is why this sits on the enforcing side of the rule: allowing a pause at all, allowing them
/// more often, or allowing longer ones are all weakenings, and all three need a token.
/// </para>
/// </summary>
public sealed class PauseOptions
{
    public bool Enabled { get; set; }

    /// <summary>How many days have to pass before this target may be paused again. 1 means daily.</summary>
    public int EveryDays { get; set; } = 1;

    /// <summary>How long one pause lasts.</summary>
    public int Minutes { get; set; } = 10;

    public override string ToString() =>
        Enabled ? $"每 {EveryDays} 天可暂停 {Minutes} 分钟" : "暂停：关";
}
