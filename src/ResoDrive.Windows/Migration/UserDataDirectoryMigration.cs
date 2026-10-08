using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResoDrive.Windows;

/// <summary>A restartable, same-volume migration. It never duplicates account data or merges independent roots.</summary>
public sealed class UserDataDirectoryMigration
{
    private sealed record Journal(Guid Id, string Source, string Destination, string Phase);
    private const string MarkerName = ".directory-migration.json";
    private readonly string _source;
    private readonly string _destination;
    private readonly string _stateDirectory;
    private readonly string _journalPath;

    public UserDataDirectoryMigration(string source, string destination)
    {
        _source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        _destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        RequireLocalDirectory(_source);
        RequireLocalDirectory(_destination);
        var parent = Path.GetDirectoryName(_source) ?? throw new ArgumentException("A data directory is required.", nameof(source));
        if (InstallationDirectories.SamePath(_source, _destination) ||
            !InstallationDirectories.SamePath(parent, Path.GetDirectoryName(_destination) ?? ""))
            throw new ArgumentException("Migration requires distinct sibling data directories on the same volume.", nameof(destination));
        _stateDirectory = Path.Combine(parent, "ResoDriveMigration");
        _journalPath = Path.Combine(_stateDirectory, "user-data-migration.json");
    }

    public async Task MigrateAsync(Func<CancellationToken, Task> prepare, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        RejectLinksToRoot(_source);
        RejectLinksToRoot(_destination);
        RejectLinksToRoot(_stateDirectory);
        if (!Directory.Exists(_source) && !File.Exists(_journalPath)) return;
        Directory.CreateDirectory(_stateDirectory);
        SensitiveFilePermissions.RestrictDirectoryToCurrentUser(_stateDirectory);
        RejectLink(Path.Combine(_stateDirectory, "migration.lock"));
        using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var journal = ReadJournal();
        if (journal is { Phase: "Complete" })
        {
            if (Directory.Exists(_source))
                throw new IOException("The old ResoDrive data folder reappeared after migration. Both folders were preserved; close older copies of ResoDrive before retrying.");
            if (HasMarker(journal)) File.Delete(Path.Combine(_destination, MarkerName));
            ArchiveCompletedJournal();
            return;
        }
        if (Directory.Exists(_source) && Directory.Exists(_destination))
            throw new IOException("Both rdrive and ResoDrive data folders exist. Migration cannot merge independent account data; both folders were preserved.");
        journal ??= new(Guid.NewGuid(), _source, _destination, "Planned");
        if (Directory.Exists(_source))
        {
            VerifyTree(_source, cancellationToken);
            var paths = new ApplicationPaths(_source);
            var wipe = new RemoteWipeStateStore(paths).Read();
            if (wipe is { Phase: not RemoteWipePhase.Completed })
                throw new IOException("Local account cleanup is pending. Let ResoDrive finish cleanup before migrating its data folder.");
            if (File.Exists(paths.ConfigSecretFile))
                _ = await new DpapiSecretStore(paths).LoadAsync(cancellationToken).ConfigureAwait(false);
            ValidateJson(_source);
            await prepare(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            VerifyTree(_source, cancellationToken);
            if (new RemoteWipeStateStore(paths).Read() is { Phase: not RemoteWipePhase.Completed })
                throw new IOException("Local account cleanup became pending during preparation. Existing data was preserved.");
            ValidateJson(_source);
            WriteJournal(journal with { Phase = "Prepared" });
            WriteAtomic(Path.Combine(_source, MarkerName), JsonSerializer.Serialize(journal));
            // A rename retains file identities, permissions and all cached content.
            // It also fails safely if a process still holds a conflicting handle.
            Directory.Move(_source, _destination);
            journal = journal with { Phase = "Renamed" };
            WriteJournal(journal);
        }
        else
        {
            if (!Directory.Exists(_destination) || !HasMarker(journal))
                throw new IOException("The interrupted data migration could not be identified. Existing data was preserved.");
        }
        VerifyTree(_destination, cancellationToken);
        // These edits are idempotent. A crash after any individual file replacement
        // resumes here, without restarting a host against partially rewritten paths.
        RewritePaths();
        WriteJournal(journal with { Phase = "Complete" });
        File.Delete(Path.Combine(_destination, MarkerName));
        ArchiveCompletedJournal();
    }

    private void ArchiveCompletedJournal()
    {
        var receipt = Path.Combine(_stateDirectory, "user-data-migration.complete.json");
        RejectLink(receipt);
        File.Move(_journalPath, receipt, overwrite: true);
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(Path.Combine(_stateDirectory, "migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when (exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) && DateTime.UtcNow < deadline)
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }

    public static string PrepareHelperDirectory()
    {
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResoDriveMigration");
        RejectLinksToRoot(state);
        Directory.CreateDirectory(state);
        SensitiveFilePermissions.RestrictDirectoryToCurrentUser(state);
        return state;
    }

    private Journal? ReadJournal()
    {
        if (!File.Exists(_journalPath)) return null;
        RejectLink(_journalPath);
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath))
            ?? throw new IOException("The directory migration journal is invalid.");
        if (journal.Id == Guid.Empty || !InstallationDirectories.SamePath(journal.Source, _source) ||
            !InstallationDirectories.SamePath(journal.Destination, _destination) ||
            journal.Phase is not ("Planned" or "Prepared" or "Renamed" or "Complete"))
            throw new IOException("The directory migration journal does not match these data folders.");
        return journal;
    }

    private bool HasMarker(Journal journal)
    {
        var path = Path.Combine(_destination, MarkerName);
        if (!File.Exists(path)) return false;
        RejectLink(path);
        var marker = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path));
        return marker?.Id == journal.Id && marker.Source == journal.Source && marker.Destination == journal.Destination;
    }

    private void RewritePaths()
    {
        foreach (var name in new[] { "settings.json", "settings.json.bak", "mount-upload-recovery.json", "mount-upload-recovery.json.bak", "ownership.json", "ownership.json.bak" })
        {
            var path = Path.Combine(_destination, name);
            if (!File.Exists(path)) continue;
            var node = ParseJson(path);
            if (name.StartsWith("settings", StringComparison.Ordinal))
            {
                foreach (var mount in node["mounts"]?.AsArray() ?? [])
                {
                    if (mount is not null) RemapArguments(mount);
                    if (mount?["target"] is { } target) Remap(target, "directoryPath");
                    foreach (var job in mount?["syncJobs"]?.AsArray() ?? [])
                        if (job is not null) { Remap(job, "localPath"); RemapArguments(job); }
                }
            }
            else
            {
                foreach (var entry in node.AsArray())
                {
                    if (entry is null) continue;
                    Remap(entry, "metadataPath");
                    Remap(entry, "executablePath");
                    if (name.StartsWith("ownership", StringComparison.Ordinal)) Remap(entry, "target");
                    RemapArguments(entry);
                }
            }
            WriteAtomic(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void Remap(JsonNode node, string property)
    {
        if (node[property]?.GetValue<string>() is { } value) node[property] = RemapPath(value);
    }

    private void RemapArguments(JsonNode node)
    {
        if (node["arguments"] is JsonArray arguments)
            for (var index = 0; index < arguments.Count; index++)
                if (arguments[index]?.GetValue<string>() is { } argument)
                    arguments[index] = RemapArgument(argument);
    }

    private string RemapArgument(string value)
    {
        var mapped = RemapPath(value);
        var oldCommand = $"\"{InstallationDirectories.LegacyExecutable}\" ";
        return mapped.StartsWith(oldCommand, StringComparison.OrdinalIgnoreCase)
            ? $"\"{InstallationDirectories.Executable}\" " + mapped[oldCommand.Length..] : mapped;
    }

    private string RemapPath(string value)
    {
        foreach (var (source, destination) in new[] { (_source, _destination), (InstallationDirectories.Legacy, InstallationDirectories.Current) })
        {
            if (value.Equals(source, StringComparison.OrdinalIgnoreCase)) return destination;
            if (value.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return destination + value[source.Length..];
        }
        return value;
    }

    private static void ValidateJson(string directory)
    {
        foreach (var name in new[] { "settings.json", "settings.json.bak", "mount-upload-recovery.json", "mount-upload-recovery.json.bak", "ownership.json", "ownership.json.bak" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            var node = ParseJson(path);
            if (name.StartsWith("settings", StringComparison.Ordinal))
            {
                if (node is not JsonObject || (node["mounts"] is not null && node["mounts"] is not JsonArray))
                    throw new IOException("The settings structure cannot be migrated safely.");
                foreach (var mount in node["mounts"]?.AsArray() ?? [])
                {
                    if (mount is not JsonObject || (mount["target"] is not null && mount["target"] is not JsonObject) ||
                        (mount["syncJobs"] is not null && mount["syncJobs"] is not JsonArray))
                        throw new IOException("The mount settings structure cannot be migrated safely.");
                    if (mount["target"] is { } target) ValidateString(target, "directoryPath");
                    ValidateArguments(mount);
                    foreach (var job in mount["syncJobs"]?.AsArray() ?? [])
                    {
                        if (job is not JsonObject) throw new IOException("The sync settings structure cannot be migrated safely.");
                        ValidateString(job, "localPath");
                        ValidateArguments(job);
                    }
                }
            }
            else if (node is not JsonArray entries || entries.Any(entry => entry is not JsonObject))
                throw new IOException("The recovery records cannot be migrated safely.");
            else
                foreach (var entry in entries)
                {
                    ValidateString(entry!, "metadataPath");
                    ValidateString(entry!, "executablePath");
                    if (name.StartsWith("ownership", StringComparison.Ordinal)) ValidateString(entry!, "target");
                    ValidateArguments(entry!);
                }
        }
    }

    private static void ValidateArguments(JsonNode node)
    {
        if (node["arguments"] is { } arguments &&
            (arguments is not JsonArray array || array.Any(value => value is not null &&
                (value is not JsonValue item || !item.TryGetValue<string>(out _)))))
            throw new IOException("Application arguments cannot be migrated safely.");
    }

    private static void ValidateString(JsonNode node, string property)
    {
        if (node[property] is { } value && (value is not JsonValue item || !item.TryGetValue<string>(out _)))
            throw new IOException("An application-owned path cannot be migrated safely.");
    }

    private static JsonNode ParseJson(string path) => JsonNode.Parse(File.ReadAllText(path),
        new JsonNodeOptions { PropertyNameCaseInsensitive = true }) ?? throw new IOException("A migration data file is empty.");

    private void WriteJournal(Journal journal) => WriteAtomic(_journalPath, JsonSerializer.Serialize(journal));

    private static void WriteAtomic(string path, string contents)
    {
        var temporary = path + ".migration.tmp";
        RejectLink(path);
        RejectLink(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void VerifyTree(string root, CancellationToken token)
    {
        RejectLink(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            token.ThrowIfCancellationRequested();
            RejectLink(path);
            if (Directory.Exists(path)) VerifyTree(path, token);
        }
    }

    private static void RejectLinksToRoot(string path)
    {
        RequireLocalDirectory(path);
        var parents = new Stack<string>();
        for (var directory = path; directory is not null; directory = Path.GetDirectoryName(directory)) parents.Push(directory);
        foreach (var directory in parents) RejectLink(directory);
    }

    private static void RequireLocalDirectory(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new IOException("Data-folder migration requires a local drive, without network or device namespaces.");
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Data-folder migration cannot access a network drive.");
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Directory migration cannot follow symbolic links or junctions. Existing data was preserved.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
