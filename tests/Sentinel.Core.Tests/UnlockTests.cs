using System.IO;
using System.Security.Cryptography;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Unlock;
using Xunit;

namespace Sentinel.Core.Tests;

public class UnlockTests : IDisposable
{
    private const string Locked = "ignored.example.com";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sentinel-unlock-" + Guid.NewGuid().ToString("N"));

    private readonly RSA _key = RSA.Create(2048);

    public UnlockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private UnlockAuthority Authority()
    {
        File.WriteAllText(UnlockAuthority.PublicKeyPathFor(ConfigPath), _key.ExportSubjectPublicKeyInfoPem());
        return UnlockAuthority.TryLoad(ConfigPath, out _)!;
    }

    /// <summary>The whole round trip the user performs: request on the machine, sign off it.</summary>
    private string TokenFor(UnlockAuthority authority, AppConfig config, TimeSpan? validity = null)
    {
        var request = authority.CreateRequest(config, validity);
        Assert.True(UnlockCrypto.TryDecodeRequest(request, out var payload));
        return UnlockCrypto.EncodeToken(payload, UnlockCrypto.Sign(_key, payload));
    }

    private static AppConfig Baseline()
    {
        var config = new AppConfig();
        config.Ui.DryRun = false;

        config.Policies.Add(new Policy
        {
            Id = "p",
            Name = "p",
            Actions = { ActionType.Notify, ActionType.Minimize },
            CooldownSeconds = 60,
        });

        config.Targets.Add(new Target
        {
            Id = "t",
            DisplayName = "浏览器",
            Enabled = true,
            ProcessNames = { "msedge" },
            Rules =
            {
                new Rule
                {
                    Id = "r",
                    PolicyId = "p",
                    Match = new MatchSpec { Type = MatchKind.Keyword, Patterns = { Locked } },
                },
            },
        });

        return config;
    }

    private static AppConfig WithAnotherTarget()
    {
        var config = Baseline();
        config.Targets.Add(new Target { Id = "t2", DisplayName = "另一个", Enabled = true });
        return config;
    }

    private static AppConfig WithoutTheTarget()
    {
        var config = Baseline();
        config.Targets.Clear();
        return config;
    }

    // ------------------------------------------------------------------ 指纹

    [Fact]
    public void Digest_survives_a_round_trip()
    {
        var config = Baseline();
        Assert.Equal(UnlockCrypto.Digest(config), UnlockCrypto.Digest(ConfigStore.Clone(config)));
    }

    [Fact]
    public void Digest_changes_when_the_config_does()
    {
        Assert.NotEqual(UnlockCrypto.Digest(Baseline()), UnlockCrypto.Digest(WithoutTheTarget()));
    }

    // ------------------------------------------------------------------ 什么时候不用令牌

    [Fact]
    public void Tightening_needs_no_token()
    {
        var authority = Authority();
        Assert.Equal(
            UnlockVerdict.NotRequired,
            authority.Authorize(Baseline(), WithAnotherTarget(), null, out _));
    }

    [Fact]
    public void An_unchanged_config_needs_no_token()
    {
        var authority = Authority();
        Assert.Equal(
            UnlockVerdict.NotRequired,
            authority.Authorize(Baseline(), Baseline(), null, out _));
    }

    // ------------------------------------------------------------------ 什么时候要

    [Fact]
    public void Relaxing_without_a_token_is_refused_with_a_reason()
    {
        var authority = Authority();

        Assert.Equal(
            UnlockVerdict.Refused,
            authority.Authorize(Baseline(), WithoutTheTarget(), null, out var reason));

        Assert.Contains("解锁令牌", reason);
    }

    [Fact]
    public void Relaxing_with_a_token_is_granted()
    {
        var authority = Authority();
        var relaxed = WithoutTheTarget();

        Assert.Equal(
            UnlockVerdict.Granted,
            authority.Authorize(Baseline(), relaxed, TokenFor(authority, relaxed), out _));
    }

    // ------------------------------------------------------------------ 令牌的边界

    [Fact]
    public void A_token_for_another_config_is_refused()
    {
        var authority = Authority();

        // Signed for a different relaxation than the one being attempted.
        var token = TokenFor(authority, Baseline());

        Assert.False(authority.Validate(WithoutTheTarget(), token, out var reason));
        Assert.Contains("另一次操作", reason);
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var authority = Authority();
        var relaxed = WithoutTheTarget();

        var token = TokenFor(authority, relaxed, TimeSpan.FromMinutes(-1));

        Assert.False(authority.Validate(relaxed, token, out var reason));
        Assert.Contains("过期", reason);
    }

    [Fact]
    public void A_token_signed_by_another_key_is_refused()
    {
        var authority = Authority();
        var relaxed = WithoutTheTarget();

        using var stranger = RSA.Create(2048);
        var request = authority.CreateRequest(relaxed);
        Assert.True(UnlockCrypto.TryDecodeRequest(request, out var payload));
        var forged = UnlockCrypto.EncodeToken(payload, UnlockCrypto.Sign(stranger, payload));

        Assert.False(authority.Validate(relaxed, forged, out var reason));
        Assert.Contains("签名", reason);
    }

    [Fact]
    public void A_tampered_payload_is_refused()
    {
        var authority = Authority();
        var relaxed = WithoutTheTarget();
        var signature = TokenFor(authority, relaxed).Split('.')[2];

        // Take the payload describing a *different* config and keep the genuine signature with it.
        // Both halves are individually valid; only the pairing is not, which is exactly what the
        // signature over the payload is there to catch.
        var otherPayload = authority.CreateRequest(Baseline())["REQ1.".Length..];
        var tampered = $"TOK1.{otherPayload}.{signature}";

        Assert.True(UnlockCrypto.TryDecodeToken(tampered, out _, out _));
        Assert.False(authority.Validate(relaxed, tampered, out var reason));
        Assert.Contains("签名", reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("TOK1..")]
    [InlineData("TOK1.notbase64!!.alsonot")]
    [InlineData("TOK1.aGVsbG8.aGVsbG8")]        // well-formed, but not a token we ever issued
    [InlineData("REQ1.aGVsbG8")]                 // a request, pasted where a token belongs
    public void Malformed_input_is_refused_rather_than_thrown(string text)
    {
        var authority = Authority();

        // Anything a caller can put on the pipe reaches this, so it must never throw.
        Assert.False(authority.Validate(WithoutTheTarget(), text, out _));
    }

    [Fact]
    public void A_corrupt_signature_does_not_throw()
    {
        var authority = Authority();
        var relaxed = WithoutTheTarget();

        var parts = TokenFor(authority, relaxed).Split('.');
        var corrupt = $"{parts[0]}.{parts[1]}.{new string('A', 344)}";

        Assert.False(authority.Validate(relaxed, corrupt, out _));
    }

    // ------------------------------------------------------------------ 请求

    [Fact]
    public void A_request_round_trips()
    {
        var authority = Authority();
        var request = authority.CreateRequest(Baseline());
        var expires = DateTimeOffset.UtcNow.Add(UnlockAuthority.DefaultValidity);

        Assert.StartsWith("REQ1.", request);
        Assert.True(UnlockCrypto.TryDecodeRequest(request, out var payload));
        Assert.True(UnlockCrypto.TryReadPayload(payload, out var digest, out var parsed, out var nonce));

        Assert.Equal(UnlockCrypto.Digest(Baseline()), digest);
        Assert.NotEmpty(nonce);
        Assert.True((parsed - expires).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Two_requests_for_the_same_config_differ()
    {
        var authority = Authority();
        Assert.NotEqual(authority.CreateRequest(Baseline()), authority.CreateRequest(Baseline()));
    }

    // ------------------------------------------------------------------ 关闭保护

    [Fact]
    public void A_release_token_is_its_own_thing()
    {
        var authority = Authority();

        var request = authority.CreateRequestFor(UnlockCrypto.ReleaseDigest);
        Assert.True(UnlockCrypto.TryDecodeRequest(request, out var payload));
        var release = UnlockCrypto.EncodeToken(payload, UnlockCrypto.Sign(_key, payload));

        Assert.True(authority.ValidateDigest(UnlockCrypto.ReleaseDigest, release, out _));

        // Taking the rule down is a different intention from loosening one edit, so the tokens are
        // not interchangeable in either direction.
        Assert.False(authority.ValidateDigest(UnlockCrypto.ReleaseDigest, TokenFor(authority, Baseline()), out var reason));
        Assert.Contains("另一次操作", reason);

        Assert.False(authority.Validate(Baseline(), release, out _));
    }

    // ------------------------------------------------------------------ 一次性

    [Fact]
    public void A_spent_token_is_remembered_across_a_restart()
    {
        var now = DateTimeOffset.Now;

        var ledger = UnlockLedger.Load(ConfigPath);
        Assert.False(ledger.Has("abc"));

        ledger.Mark("abc", now);
        ledger.Save(ConfigPath);

        Assert.True(UnlockLedger.Load(ConfigPath).Has("abc"));
    }

    [Fact]
    public void Old_entries_are_dropped_but_not_while_a_token_could_still_arrive()
    {
        var now = DateTimeOffset.Now;

        var ledger = UnlockLedger.Load(ConfigPath);
        ledger.Mark("ancient", now - UnlockLedger.KeepFor - TimeSpan.FromDays(1));
        ledger.Mark("recent", now - TimeSpan.FromHours(1));

        // Marking prunes; everything still within a token's possible life stays, so nothing can be
        // presented after its record has been thrown away.
        ledger.Mark("new", now);

        Assert.False(ledger.Has("ancient"));
        Assert.True(ledger.Has("recent"));
        Assert.True(ledger.Has("new"));
    }

    [Fact]
    public void An_unreadable_ledger_reads_as_empty()
    {
        File.WriteAllText(UnlockLedger.PathFor(ConfigPath), "{ not json");

        // The weaker direction, chosen deliberately: refusing every token because a bookkeeping file
        // went missing would lock the user out of their own config over nothing.
        Assert.False(UnlockLedger.Load(ConfigPath).Has("abc"));
    }

    [Fact]
    public void An_accepted_token_reports_which_one_it_was()
    {
        var authority = Authority();
        var relaxed = WithoutTheTarget();

        // The nonce is what the ledger records, so the caller has to be told which token it was.
        var verdict = authority.Authorize(Baseline(), relaxed, TokenFor(authority, relaxed), out _, out var nonce);

        Assert.Equal(UnlockVerdict.Granted, verdict);
        Assert.False(string.IsNullOrEmpty(nonce));
    }

    [Fact]
    public void An_edit_that_needs_no_token_reports_none()
    {
        var authority = Authority();

        // Nothing was spent, so there is nothing to record — marking a tightening as spent would
        // fill the ledger with entries that mean nothing.
        var verdict = authority.Authorize(Baseline(), WithAnotherTarget(), null, out _, out var nonce);

        Assert.Equal(UnlockVerdict.NotRequired, verdict);
        Assert.Null(nonce);
    }

    // ------------------------------------------------------------------ 未配置

    [Fact]
    public void No_public_key_means_no_authority()
    {
        Assert.Null(UnlockAuthority.TryLoad(ConfigPath, out var reason));
        Assert.Contains("未配置", reason);
    }
}
