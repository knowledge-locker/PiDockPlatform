using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
namespace PiDock.Web.Shared;

public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(20));
    public static string Encode(byte[] bytes)
    {
        var result = new System.Text.StringBuilder(); int bits = 0, buffer = 0;
        foreach (var b in bytes) { buffer = (buffer << 8) | b; bits += 8; while (bits >= 5) { bits -= 5; result.Append(Alphabet[(buffer >> bits) & 31]); } }
        if (bits > 0) result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }
    private static byte[] Decode(string text)
    {
        var result = new List<byte>(); int buffer = 0, bits = 0;
        foreach (var c in text) { int n = Alphabet.IndexOf(c); if (n < 0) throw new FormatException("Invalid secret."); buffer = (buffer << 5) | n; bits += 5; if (bits >= 8) { bits -= 8; result.Add((byte)(buffer >> bits)); } }
        return result.ToArray();
    }
    public static string Code(string secret, long step, int digits = 6)
    {
        if (digits is < 6 or > 8) throw new ArgumentOutOfRangeException(nameof(digits));
        Span<byte> counter = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(Decode(secret), counter); int offset = hash[^1] & 15;
        int value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % (int)Math.Pow(10, digits)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }
    public static long? Match(string secret, string code, DateTimeOffset now, long lastAccepted = -1)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        long step = now.ToUnixTimeSeconds() / 30;
        for (long candidate = step - 1; candidate <= step + 1; candidate++)
            if (candidate > lastAccepted && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(Code(secret, candidate)))) return candidate;
        return null;
    }
}
