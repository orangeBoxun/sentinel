namespace Sentinel.Agent;

/// <summary>
/// A dropdown entry: a value plus the text shown for it.
///
/// Top-level and public on purpose. WPF resolves DisplayMemberPath by reflection, and a private
/// nested type is the kind of thing that works until it doesn't — the combo boxes would render
/// blank rows with no error anywhere.
/// </summary>
public sealed record Choice<T>(T Value, string Display)
{
    /// <summary>
    /// WPF's automation peers fall back to ToString() for an item's accessible name, so without
    /// this a combo box announces itself as "Choice`1" to a screen reader.
    /// </summary>
    public override string ToString() => Display;
}
