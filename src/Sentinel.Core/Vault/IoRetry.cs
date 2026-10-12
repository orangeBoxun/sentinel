namespace Sentinel.Core.Vault;

/// <summary>
/// Retries an IO step that Windows refuses while somebody else has the file open for a moment — an
/// indexer, a virus scanner, a backup agent walking the disk.
///
/// <para>
/// This is not defensive padding. A delete or a move that fails this way is a failure the user sees
/// as "it said it deleted it but the thing is still there" or "restore just doesn't work", and the
/// cause is invisible from the outside. The operation is genuinely retryable, so it is worth
/// retrying rather than reporting.
/// </para>
/// <para>
/// Deliberately short: long enough to ride out a scanner touching a file, short enough that a real
/// failure is still reported while the user is looking at the screen.
/// </para>
/// </summary>
internal static class IoRetry
{
    /// <summary>
    /// Ten tries over about half a second. Long enough to ride out a scanner holding a file, short
    /// enough that a real failure is still reported while the user is looking at the screen — and
    /// the alternative for a delete is a file the user believes is gone and is not.
    /// </summary>
    private const int Attempts = 10;

    private const int DelayMs = 60;

    public static bool Try(Action operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Attempts - 1)
            {
                Thread.Sleep(DelayMs);
            }
            catch
            {
                return false;
            }
        }
    }
}
