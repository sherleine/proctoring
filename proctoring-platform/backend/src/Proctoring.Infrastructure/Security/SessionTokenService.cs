using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Proctoring.Application.Sessions;
using Proctoring.Domain.Sessions;

namespace Proctoring.Infrastructure.Security;

/// <summary>
/// Session tokens: "tok_" + 32 bytes in base64url = 47 characters.
///
/// - Ordinary creates: 32 bytes from the cryptographically secure RNG.
/// - Idempotent creates: HMAC-SHA256(server secret, label | sessionId | salt), with a 32-byte random salt.
///   Without the secret the salt is useless; with it, the same token can be recreated for a retried request.
///
/// Stored: SHA-256(token) only. Why plain SHA-256 and not bcrypt/argon2? Those are slow on purpose because
/// passwords are guessable. A 256-bit random (or HMAC) token cannot be guessed, so a fast hash already makes a
/// leaked database useless for impersonation, and keeps every request cheap.
/// </summary>
public sealed class SessionTokenService(SessionTokenSecret secret) : ISessionTokenService
{
    public const string Prefix = "tok_";
    private const int TokenBytes = 32;
    private static readonly int EncodedLength = Prefix.Length + Base64Url.GetEncodedLength(TokenBytes);
    private static readonly byte[] DerivationLabel = Encoding.UTF8.GetBytes("proctoring.session-token.v1\n");

    public (string Token, byte[] Hash) Generate()
    {
        Span<byte> random = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(random);
        var token = Prefix + Base64Url.EncodeToString(random);
        return (token, Hash(token));
    }

    public byte[] NewDerivationSalt() => RandomNumberGenerator.GetBytes(Session.DerivationSaltLength);

    public string Derive(string sessionId, byte[] derivationSalt)
    {
        if (derivationSalt.Length != Session.DerivationSaltLength) throw new ArgumentException("Invalid salt", nameof(derivationSalt));
        var id = Encoding.UTF8.GetBytes(sessionId + "\n");
        var message = new byte[DerivationLabel.Length + id.Length + derivationSalt.Length];
        DerivationLabel.CopyTo(message, 0);
        id.CopyTo(message, DerivationLabel.Length);
        derivationSalt.CopyTo(message, DerivationLabel.Length + id.Length);
        return Prefix + Base64Url.EncodeToString(HMACSHA256.HashData(secret.Key, message));
    }

    public byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public bool Verify(string presentedToken, byte[] storedHash)
    {
        if (storedHash.Length != Session.TokenHashLength) return false;
        // Hash whatever was presented so timing does not depend on the format check, then compare in
        // constant time: the comparison does not stop at the first differing byte.
        var presentedHash = Hash(presentedToken ?? string.Empty);
        var formatOk = IsWellFormed(presentedToken);
        return CryptographicOperations.FixedTimeEquals(presentedHash, storedHash) & formatOk;
    }

    /// <summary>True when the value has the token format (used to reject malformed credentials with 401).</summary>
    public static bool IsWellFormed(string? token) =>
        token is not null
        && token.Length == EncodedLength
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && Base64Url.IsValid(token.AsSpan(Prefix.Length));
}
