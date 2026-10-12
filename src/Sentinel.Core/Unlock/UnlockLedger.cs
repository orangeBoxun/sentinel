using System.Text.Json;

namespace Sentinel.Core.Unlock;

/// <summary>
/// The tokens that have already been spent.
///
/// <para>
/// A token covers one specific configuration, which sounds like it makes replay pointless: apply it
/// and the config is that config, so presenting it again is not a change at all. It is not
/// pointless. Undo the change — free, because tightening always is — and the token works again:
/// one trip to the key could re-disable the same target every evening for as long as its expiry
/// lasts.
/// </para>
/// <para>
/// So a token is spent once. The record lives in the install directory, where the user cannot edit
/// it, and it is pruned by age: an entry only has to outlive the token it refers to, after which
/// the expiry is what refuses it.
/// </para>
/// </summary>
public sealed class UnlockLedger
{
    public const string FileName = ".unlock-used.json";

    /// <summary>
    /// How long an entry is kept. Comfortably longer than any token the Service issues, so nothing
    /// is ever pruned while it could still be presented.
    /// </summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<Entry> Used { get; set; } = new();

    public sealed class Entry
    {
        public string Nonce { get; set; } = "";

        public string At { get; set; } = "";
    }

    public static string PathFor(string configPath) =>
        Path.Combine(Path.GetDirectoryName(configPath) ?? ".", FileName);

    public static UnlockLedger Load(string configPath)
    {
        try
        {
            var path = PathFor(configPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UnlockLedger>(File.ReadAllText(path), Options) ?? new UnlockLedger()
                : new UnlockLedger();
        }
        catch
        {
            // Unreadable reads as empty. That is the weaker direction — a token could be spent twice
            // — but the alternative is refusing every token because a bookkeeping file went missing,
            // which locks the user out of their own config over nothing.
            return new UnlockLedger();
        }
    }

    public bool Has(string nonce) =>
        !string.IsNullOrEmpty(nonce) && Used.Any(entry => entry.Nonce.Equals(nonce, StringComparison.Ordinal));

    public void Mark(string nonce, DateTimeOffset now)
    {
        Used.RemoveAll(entry => !DateTimeOffset.TryParse(entry.At, out var at) || now - at > KeepFor);
        Used.Add(new Entry { Nonce = nonce, At = now.ToUniversalTime().ToString("O") });
    }

    public void Save(string configPath)
    {
        try
        {
            var path = PathFor(configPath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Losing the record only means a token might be spent twice. The file is rewritten on
            // the next successful use, so failing the operation over it would be worse than the
            // thing it guards against.
        }
    }
}
