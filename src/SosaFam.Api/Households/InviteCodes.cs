using System.Security.Cryptography;
using System.Text;

namespace SosaFam.Api.Households;

/// <summary>
/// Invite codes look like "K7QM-2XPA": 8 characters from an alphabet without look-alikes
/// (no 0/O, 1/I/L), about 850 billion combinations. Only the SHA-256 hash is stored.
/// </summary>
public static class InviteCodes
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{chars[..4]}-{chars[4..]}";
    }

    public static string Normalize(string code) =>
        new(code.ToUpperInvariant().Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());

    public static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)));
}
