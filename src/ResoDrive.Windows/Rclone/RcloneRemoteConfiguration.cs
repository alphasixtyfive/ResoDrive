using System.Security.Cryptography;
using System.Text;

namespace ResoDrive.Windows;

internal interface IRcloneRemoteConfigurationAccess
{
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAsync(
        string executablePath, string configPath, string passwordCommand,
        IReadOnlyCollection<string> remoteNames, CancellationToken token);
}

/// <summary>Reads selected remotes through an isolated rclone process without changing credentials.</summary>
internal sealed class RcloneRemoteConfigurationAccess : IRcloneRemoteConfigurationAccess
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    private readonly IRcloneRcSessionFactory _sessions;

    public RcloneRemoteConfigurationAccess() : this(new RcloneRcSessionFactory()) { }
    internal RcloneRemoteConfigurationAccess(IRcloneRcSessionFactory sessions) =>
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAsync(
        string executablePath, string configPath, string passwordCommand,
        IReadOnlyCollection<string> remoteNames, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(remoteNames);
        ValidatePaths(executablePath, configPath, passwordCommand);
        if (remoteNames.Count > 1024 || remoteNames.Any(name => !ValidRemoteName(name)))
            throw new InvalidOperationException("The selected remote names are invalid.");
        token.ThrowIfCancellationRequested();
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (remoteNames.Count == 0) return result;
        try
        {
            await using var session = await _sessions.StartAsync(
                Path.GetFullPath(executablePath), Path.GetFullPath(configPath), passwordCommand, token).ConfigureAwait(false);
            var available = (await session.ListRemotesAsync(token).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            foreach (var name in remoteNames.Distinct(StringComparer.Ordinal).Where(available.Contains))
            {
                token.ThrowIfCancellationRequested();
                result.Add(name, await session.GetConfigAsync(name, token).ConfigureAwait(false));
            }
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The isolated rclone configuration read timed out.");
        }
    }

    private static void ValidatePaths(string executablePath, string configPath, string passwordCommand)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath) ||
            string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath) ||
            string.IsNullOrWhiteSpace(passwordCommand))
            throw new InvalidOperationException("The protected rclone configuration is not available.");
        RequireEncryptedConfig(configPath);
    }

    private static void RequireEncryptedConfig(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("The protected rclone configuration path is invalid.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumConfigBytes)
            throw new InvalidOperationException("The protected rclone configuration has an invalid size.");
        Span<byte> prefix = stackalloc byte[4096];
        var length = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        var text = Encoding.UTF8.GetString(prefix[..length]);
        foreach (var line in text.Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#') || value.StartsWith(';')) continue;
            if (value == "RCLONE_ENCRYPT_V0:") return;
            break;
        }
        throw new InvalidOperationException("The rclone configuration must remain encrypted.");
    }

    private static bool ValidRemoteName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl) &&
        !name.Contains(':', StringComparison.Ordinal) && !name.Contains('/', StringComparison.Ordinal) &&
        !name.Contains('\\', StringComparison.Ordinal);
}

/// <summary>Decodes rclone's reversible password obfuscation; this is not encryption at rest.</summary>
internal static class RcloneObscuredSecret
{
    private const int MaximumPasswordBytes = 8192;
    private const string InvalidSecretMessage = "The stored rclone password is not a valid obscured credential.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Reveal(string value)
    {
        // rclone uses a 16-byte IV followed by AES-CTR ciphertext, encoded as unpadded base64url.
        // Format/key: https://github.com/rclone/rclone/blob/master/fs/config/obscure/obscure.go
        if (string.IsNullOrEmpty(value) || value.Length > ((MaximumPasswordBytes + 16) * 4 + 2) / 3 ||
            value.Length % 4 == 1 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException(InvalidSecretMessage);

        var decoded = new byte[(value.Length * 3 + 3) / 4];
        var encoded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
        Span<byte> counter = stackalloc byte[16];
        Span<byte> mask = stackalloc byte[16];
        byte[] key = [0x9c, 0x93, 0x5b, 0x48, 0x73, 0x0a, 0x55, 0x4d,
            0x6b, 0xfd, 0x7c, 0x63, 0xc8, 0x86, 0xa9, 0x2b,
            0xd3, 0x90, 0x19, 0x8e, 0xb8, 0x12, 0x8a, 0xfb,
            0xf4, 0xde, 0x16, 0x2b, 0x8b, 0x95, 0xf6, 0x38];
        try
        {
            if (!Convert.TryFromBase64String(encoded, decoded, out var length) || length < 16 ||
                length - 16 > MaximumPasswordBytes ||
                !string.Equals(Convert.ToBase64String(decoded, 0, length).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                    value, StringComparison.Ordinal))
                throw new InvalidOperationException(InvalidSecretMessage);
            decoded.AsSpan(0, 16).CopyTo(counter);
            using var aes = Aes.Create();
            aes.Key = key;
            for (var offset = 16; offset < length; offset += 16)
            {
                aes.EncryptEcb(counter, mask, PaddingMode.None);
                var count = Math.Min(16, length - offset);
                for (var index = 0; index < count; index++) decoded[offset + index] ^= mask[index];
                for (var index = counter.Length - 1; index >= 0 && ++counter[index] == 0; index--) { }
            }
            var result = StrictUtf8.GetString(decoded, 16, length - 16);
            if (result.Any(char.IsControl)) throw new InvalidOperationException(InvalidSecretMessage);
            return result;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException(InvalidSecretMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
            CryptographicOperations.ZeroMemory(counter);
            CryptographicOperations.ZeroMemory(mask);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
