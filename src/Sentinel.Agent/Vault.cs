using System.Windows;

namespace Sentinel.Agent;

/// <summary>
/// The single instance of the vault window.
///
/// <para>
/// Both the tray menu and the tab open it, and they must open the same one. Two windows would mean
/// two unlocked sessions, and — more to the point — the second one would be a copy of the first
/// that nobody closed, which is not a thing a vault should be able to do.
/// </para>
/// </summary>
internal static class Vault
{
    private static VaultWindow? _window;

    /// <summary>Set when the window closes, so a caller can refresh whatever showed its status.</summary>
    public static event Action? Closed;

    public static void Show(AgentState state)
    {
        if (_window is null)
        {
            _window = new VaultWindow(state);
            _window.Closed += (_, _) =>
            {
                _window = null;
                Closed?.Invoke();
            };
            _window.Show();
        }
        else
        {
            _window.Activate();
        }
    }
}
