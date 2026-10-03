using System.Text.Json;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Validation;

namespace ResoDrive.Windows;

internal sealed record MountRecoveryEntry(Guid MountId, string Source, string[] Arguments, string DisplayName, string? MetadataPath,
    string? ConfigFingerprint = null, bool ArgumentsKnown = true);

/// <summary>A launch intent remains until a verified clean disconnect. A crash
/// before the next health poll therefore cannot erase recovery information.</summary>
internal sealed class MountRecoveryStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, MountRecoveryEntry> _entries;
    public MountRecoveryStore(ApplicationPaths paths)
    {
        _path = Path.Combine(paths.Root, "mount-upload-recovery.json");
        _entries = Load(_path).ToDictionary(x => x.MountId);
    }
    public IReadOnlyList<MountRecoveryEntry> GetEntries()
    {
        lock (_entries) return _entries.Values.ToArray();
    }
    public MountRecoveryEntry? Find(MountId id)
    {
        lock (_entries) return _entries.GetValueOrDefault(id.Value);
    }
    public async Task RecordAsync(MountDefinition definition, string? metadataPath, CancellationToken token, string? configFingerprint = null)
    {
        var previousEntry = Find(definition.Id);
        if (metadataPath is null && Find(definition.Id) is { } existing && Matches(existing, definition))
            metadataPath = existing.MetadataPath;
        configFingerprint ??= previousEntry?.ConfigFingerprint;
        var entry = new MountRecoveryEntry(definition.Id.Value, Source(definition), definition.Arguments.ToArray(), definition.DisplayName, metadataPath, configFingerprint);
        if (Find(definition.Id) is { } previous && Matches(previous, definition) &&
            previous.MetadataPath == metadataPath && previous.DisplayName == definition.DisplayName && previous.ConfigFingerprint == configFingerprint)
            return;
        await UpdateAsync(entry, definition.Id.Value, token).ConfigureAwait(false);
    }
    public Task RemoveAsync(MountId id, CancellationToken token) => UpdateAsync(null, id.Value, token);
    public Task RecordOrphanAsync(OwnedMount owned, CancellationToken token) =>
        Find(new MountId(owned.MountId)) is not null ? Task.CompletedTask :
            UpdateAsync(new(owned.MountId, owned.Source, [], "Recovered drive", null, ArgumentsKnown: false), owned.MountId, token);
    public static bool Matches(MountRecoveryEntry entry, MountDefinition definition) =>
        entry.Source == Source(definition) && (!entry.ArgumentsKnown || entry.Arguments.SequenceEqual(definition.Arguments, StringComparer.Ordinal));
    private static string Source(MountDefinition definition) => RemotePathUtility.FormatSource(definition.RemoteName, definition.RemotePath);
    private async Task UpdateAsync(MountRecoveryEntry? entry, Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Dictionary<Guid, MountRecoveryEntry> updated;
            lock (_entries) updated = new(_entries);
            if (entry is null) updated.Remove(id); else updated[id] = entry;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, updated.Values.ToArray(), Options, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path);
            lock (_entries)
            {
                _entries.Clear();
                foreach (var item in updated) _entries.Add(item.Key, item.Value);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
    private static MountRecoveryEntry[] Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 1024 * 1024) throw new IOException("Upload recovery records exceed the inspection limit.");
                var entries = JsonSerializer.Deserialize<MountRecoveryEntry[]>(stream, Options) ?? throw new JsonException();
                if (entries.Any(x => x is null || x.MountId == Guid.Empty || string.IsNullOrWhiteSpace(x.Source) ||
                    x.Arguments is null || x.DisplayName is null) || entries.Select(x => x.MountId).Distinct().Count() != entries.Length)
                    throw new JsonException("Invalid upload recovery records.");
                return entries;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { }
        }
        if (File.Exists(path) || File.Exists(path + ".bak"))
            throw new IOException("Upload recovery records are unreadable. Preserve the cache and repair the recovery records before continuing.");
        return [];
    }
    public void Dispose() => _gate.Dispose();
}

internal sealed record PersistedMountControl(int ProcessId, DateTime StartTimeUtc, string Address, string User, string Password);

internal sealed class MountControlStore(ApplicationPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private string FilePath(Guid id) => Path.Combine(paths.Root, "mount-controls", id.ToString("N") + ".dpapi");
    public async Task SaveAsync(OwnedMount mount, string address, string user, string password, CancellationToken token)
    {
        var path = FilePath(mount.MountId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var value = new PersistedMountControl(mount.ProcessId, mount.StartTimeUtc, address, user, password);
            await new DpapiSecretStore(paths).SaveProtectedFileAsync(JsonSerializer.Serialize(value, Options), temporary, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<PersistedMountControl?> LoadAsync(OwnedMount mount, CancellationToken token)
    {
        try
        {
            var json = await new DpapiSecretStore(paths).LoadProtectedFileAsync(FilePath(mount.MountId), token).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize<PersistedMountControl>(json, Options);
            if (value is null || value.ProcessId != mount.ProcessId || value.StartTimeUtc != mount.StartTimeUtc ||
                string.IsNullOrWhiteSpace(value.User) || string.IsNullOrWhiteSpace(value.Password) ||
                !Uri.TryCreate("http://" + value.Address + "/", UriKind.Absolute, out var endpoint) ||
                endpoint.Host != "127.0.0.1" || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/")
                return null;
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        { return null; }
    }
    public void Remove(Guid id)
    {
        try { File.Delete(FilePath(id)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
