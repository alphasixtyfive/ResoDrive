using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ResoDrive.Windows;

internal interface IConfigSecretStore
{
    bool Exists { get; }
    Task<string> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveProtectedFileAsync(string password, string destinationPath, CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed partial class DpapiSecretStore : IConfigSecretStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private const int MaximumEncodedFileBytes = 16 * 1024;
    private const int MaximumPasswordBytes = 4 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ApplicationPaths _paths;
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes("rdrive/rclone/config-password/v1");

    public DpapiSecretStore(ApplicationPaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public bool Exists => File.Exists(_paths.ConfigSecretFile);

    public void Delete()
    {
        if (File.Exists(_paths.ConfigSecretFile))
        {
            File.Delete(_paths.ConfigSecretFile);
        }
    }

    public static string CreateRandomPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        try
        {
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task SaveAsync(string password, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var stagedPath = _paths.ConfigSecretFile + $".{Guid.NewGuid():N}.tmp";
        await SaveProtectedFileAsync(password, stagedPath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.ConfigSecretFile))
            {
                File.Replace(stagedPath, _paths.ConfigSecretFile, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(stagedPath, _paths.ConfigSecretFile);
            }
        }
        finally
        {
            if (File.Exists(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }
    }

    public async Task SaveProtectedFileAsync(
        string password,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        _paths.EnsureCreated();
        var plaintext = Encoding.UTF8.GetBytes(password);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = Crypt(plaintext, _entropy, protect: true);
            await File.WriteAllTextAsync(
                destinationPath,
                Convert.ToBase64String(protectedBytes),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            SensitiveFilePermissions.RestrictToCurrentUser(destinationPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

        }
    }

    public async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        var password = await LoadProtectedFileAsync(
            _paths.ConfigSecretFile,
            cancellationToken).ConfigureAwait(false);
        SensitiveFilePermissions.RestrictToCurrentUser(_paths.ConfigSecretFile);
        return password;
    }

    public Task<string> LoadProtectedFileAsync(string path, CancellationToken cancellationToken = default) =>
        LoadProtectedFileAsync(path, MaximumPasswordBytes, cancellationToken);

    internal async Task<string> LoadProtectedFileAsync(
        string path,
        int maximumPlaintextBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPlaintextBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPlaintextBytes, 64 * 1024);
        var info = new FileInfo(Path.GetFullPath(path));
        var maximumEncodedBytes = Math.Max(MaximumEncodedFileBytes, maximumPlaintextBytes * 2 + 4096);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumEncodedBytes)
            throw new CryptographicException("The protected rclone secret has an invalid size.");
        var encoded = await File.ReadAllTextAsync(info.FullName, cancellationToken).ConfigureAwait(false);
        var protectedBytes = Convert.FromBase64String(encoded.Trim());
        byte[]? plaintext = null;
        try
        {
            plaintext = Crypt(protectedBytes, _entropy, protect: false);
            if (plaintext.Length <= 0 || plaintext.Length > maximumPlaintextBytes)
                throw new CryptographicException("The protected rclone secret has invalid content.");
            var password = StrictUtf8.GetString(plaintext);
            if (string.IsNullOrWhiteSpace(password) || password.Any(char.IsControl))
                throw new CryptographicException("The protected rclone secret has invalid content.");
            return password;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] Crypt(byte[] data, byte[] entropy, bool protect)
    {
        var input = DataBlob.FromBytes(data);
        var optionalEntropy = DataBlob.FromBytes(entropy);
        var output = default(DataBlob);
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref input,
                    null,
                    ref optionalEntropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref input,
                    null,
                    ref optionalEntropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!succeeded)
            {
                throw new InvalidOperationException($"Windows could not protect the rclone secret (error {Marshal.GetLastWin32Error()}).");
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            input.Free();
            optionalEntropy.Free();
            if (output.Data != IntPtr.Zero)
            {
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public static DataBlob FromBytes(byte[] bytes)
        {
            var result = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, result.Data, bytes.Length);
            return result;
        }

        public void Free()
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            var zeros = new byte[Size];
            Marshal.Copy(zeros, 0, Data, Size);
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
            Size = 0;
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr memory);
}
