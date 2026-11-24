using System.Security.Cryptography;
using Proctoring.Application.Sessions;

namespace Proctoring.Infrastructure.Security;

/// <summary>
/// Session IDs: "ses_" + a 26-character ULID (48-bit millisecond timestamp + 80 random bits,
/// Crockford base32). IDs sort by creation time, which keeps database indexes efficient, and the random
/// part makes them unguessable. Matches the pattern ^ses_[A-Za-z0-9]{20,}$ in session.v1.json.
/// </summary>
public sealed class SessionIdGenerator(TimeProvider clock) : ISessionIdGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public string NewId()
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = clock.GetUtcNow().ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }
        RandomNumberGenerator.Fill(bytes[6..]);
        return "ses_" + EncodeCrockford(bytes);
    }

    private static string EncodeCrockford(ReadOnlySpan<byte> bytes)
    {
        // 128 bits -> 26 base32 characters (the first character carries the top 3 bits).
        var value = new UInt128(
            BitConverter.ToUInt64([.. bytes[..8].ToArray().Reverse()]),
            BitConverter.ToUInt64([.. bytes[8..].ToArray().Reverse()]));
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return new string(chars);
    }
}
