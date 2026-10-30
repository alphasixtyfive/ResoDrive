using System.Buffers.Binary;
using System.Text;

namespace ResoDrive.Core.Validation;

/// <summary>Validates a supplied pinned host key in canonical OpenSSH public-key form.</summary>
public static class SftpHostKeyValidator
{
    private static readonly HashSet<string> SupportedAlgorithms = new(StringComparer.Ordinal)
    {
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521"
    };

    // Optional pins are handled by callers; an actual pin must be one complete,
    // single-line "algorithm base64" value without comments or hidden whitespace.
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl)) return false;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || value != $"{parts[0]} {parts[1]}" || !SupportedAlgorithms.Contains(parts[0]))
            return false;
        var algorithm = parts[0];
        var encodedKey = parts[1];
        if (encodedKey.Length is < 40 or > 24_000 || encodedKey.Any(char.IsWhiteSpace)) return false;
        try
        {
            var key = Convert.FromBase64String(encodedKey);
            if (key.Length is < 32 or > 16_384) return false;
            // SSH key blobs begin with a length-prefixed algorithm name. A valid
            // base64 string alone cannot establish that it matches the declared pin.
            var algorithmLength = BinaryPrimitives.ReadInt32BigEndian(key);
            return algorithmLength == algorithm.Length && key.Length >= 4 + algorithmLength &&
                Encoding.ASCII.GetString(key, 4, algorithmLength).Equals(algorithm, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }
}
