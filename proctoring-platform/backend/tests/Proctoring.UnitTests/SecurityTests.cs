using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Proctoring.Application.Sessions;
using Proctoring.Infrastructure.Security;

namespace Proctoring.UnitTests;

public class SessionTokenServiceTests
{
    internal static SessionTokenSecret NewSecret() => new(RandomNumberGenerator.GetBytes(32), isEphemeral: false);

    private readonly SessionTokenService _tokens = new(NewSecret());

    [Fact]
    public void Generated_token_has_documented_format()
    {
        var (token, _) = _tokens.Generate();
        Assert.Matches("^tok_[A-Za-z0-9_-]{43}$", token);
        Assert.True(SessionTokenService.IsWellFormed(token));
    }

    [Fact]
    public void Generated_tokens_are_unique()
    {
        var set = new HashSet<string>();
        for (var i = 0; i < 10_000; i++) Assert.True(set.Add(_tokens.Generate().Token));
    }

    [Fact]
    public void Stored_value_is_the_sha256_hash_not_the_token()
    {
        var (token, hash) = _tokens.Generate();
        Assert.Equal(32, hash.Length);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), hash);
    }

    [Fact]
    public void Verify_accepts_the_matching_token()
    {
        var (token, hash) = _tokens.Generate();
        Assert.True(_tokens.Verify(token, hash));
    }

    [Fact]
    public void Verify_rejects_other_tampered_and_malformed_tokens()
    {
        var (token, hash) = _tokens.Generate();
        var (other, _) = _tokens.Generate();
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        foreach (var bad in new[] { other, tampered, "", " ", token[4..], token.ToUpperInvariant(), token + "x", "Bearer " + token })
            Assert.False(_tokens.Verify(bad, hash), $"accepted '{bad}'");
        Assert.False(_tokens.Verify(null!, hash));
    }

    [Fact]
    public void Verify_rejects_a_hash_of_the_wrong_length()
    {
        var (token, _) = _tokens.Generate();
        Assert.False(_tokens.Verify(token, new byte[16]));
    }

    // ------------------------------------------------------------------ derived tokens (idempotent creates)

    [Fact]
    public void Derived_token_is_deterministic_for_the_same_session_salt_and_secret()
    {
        var salt = _tokens.NewDerivationSalt();
        var a = _tokens.Derive("ses_01JABCDEFGHJKMNPQRSTVWXYZ", salt);
        var b = _tokens.Derive("ses_01JABCDEFGHJKMNPQRSTVWXYZ", salt);
        Assert.Equal(a, b);
        Assert.Matches("^tok_[A-Za-z0-9_-]{43}$", a);
        Assert.True(_tokens.Verify(a, _tokens.Hash(a)));
    }

    [Fact]
    public void Derived_token_changes_with_salt_session_or_secret()
    {
        var salt = _tokens.NewDerivationSalt();
        var token = _tokens.Derive("ses_01JABCDEFGHJKMNPQRSTVWXYZ", salt);
        Assert.NotEqual(token, _tokens.Derive("ses_01JABCDEFGHJKMNPQRSTVWXYZ", _tokens.NewDerivationSalt()));
        Assert.NotEqual(token, _tokens.Derive("ses_01JZZZZZZZZZZZZZZZZZZZZZZZ", salt));
        Assert.NotEqual(token, new SessionTokenService(NewSecret()).Derive("ses_01JABCDEFGHJKMNPQRSTVWXYZ", salt));
    }

    [Fact]
    public void Derivation_salts_are_random_and_32_bytes()
    {
        var a = _tokens.NewDerivationSalt();
        var b = _tokens.NewDerivationSalt();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Secret_is_validated_and_never_printed()
    {
        Assert.Throws<ArgumentException>(() => new SessionTokenSecret(new byte[16], false));
        Assert.Equal("[redacted]", NewSecret().ToString());
    }
}

public class SessionIdGeneratorTests
{
    [Fact]
    public void Ids_match_contract_pattern_and_are_unique()
    {
        var gen = new SessionIdGenerator(TimeProvider.System);
        var ids = Enumerable.Range(0, 5000).Select(_ => gen.NewId()).ToList();
        Assert.All(ids, id => Assert.Matches(new Regex("^ses_[0-9A-HJKMNP-TV-Z]{26}$"), id));
        Assert.All(ids, id => Assert.Matches("^ses_[A-Za-z0-9]{20,}$", id)); // session.v1.json pattern
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Ids_sort_by_creation_time()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
        var gen = new SessionIdGenerator(clock);
        var ids = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            ids.Add(gen.NewId());
            clock.Advance(TimeSpan.FromMilliseconds(3));
        }
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);
    }
}
