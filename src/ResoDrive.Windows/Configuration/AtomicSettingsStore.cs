using System.Text.Json;
using System.Text.Json.Serialization;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Validation;

namespace ResoDrive.Windows;

public sealed class AtomicSettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly AccountDataGuard _accountData;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicSettingsStore(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();
        _path = paths.SettingsFile;
        _accountData = new(paths);
    }

    public async Task<OperationResult<ManagerSettings>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<ManagerSettings>("settings.access_denied", exception.Message);
        }
        catch (IOException exception)
        {
            return Result.Failure<ManagerSettings>("settings.io", exception.Message, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<ManagerSettings>> SaveAsync(
        ManagerSettings settings,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var accountLease = await _accountData.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var candidate = ValidateSettings(settings);
            if (!candidate.Succeeded)
            {
                return Result.Failure<ManagerSettings>(
                    candidate.Error!.Code,
                    candidate.Error.Message);
            }

            var existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!existing.Succeeded || existing.Value is null)
            {
                return Result.Failure<ManagerSettings>(
                    existing.Error?.Code ?? "settings.load_failed",
                    existing.Error?.Message ?? "Settings could not be loaded.");
            }

            if (existing.Value.Revision != expectedRevision)
            {
                return Result.Failure<ManagerSettings>(
                    "settings.revision_conflict",
                    "Settings changed in another application process. Reload and try again.");
            }

            var updated = settings with
            {
                SchemaVersion = ManagerSettings.CurrentSchemaVersion,
                Revision = checked(expectedRevision + 1)
            };
            await WriteAtomicallyAsync(updated, cancellationToken).ConfigureAwait(false);
            return Result.Success(updated);
        }
        catch (IOException exception)
        {
            return Result.Failure<ManagerSettings>("settings.io", exception.Message, true);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<ManagerSettings>("settings.access_denied", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<ManagerSettings>> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var accountLease = await _accountData.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var source = Path.GetFullPath(sourcePath);
            if (!File.Exists(source))
            {
                return Result.Failure<ManagerSettings>(
                    "settings.import_missing",
                    "The selected settings file no longer exists.");
            }
            if (string.Equals(source, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<ManagerSettings>(
                    "settings.import_same_file",
                    "Choose an exported settings file instead of the active ResoDrive settings file.");
            }

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            var stagedPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.import");
            var previousPath = Path.Combine(
                directory,
                $"settings.pre-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            try
            {
                /* Validate the private staged bytes that will become active, not a
                   separately reopened source that could change between operations. */
                File.Copy(source, stagedPath, overwrite: false);
                var imported = await TryReadAsync(stagedPath, cancellationToken).ConfigureAwait(false);
                if (imported is null)
                {
                    return Result.Failure<ManagerSettings>(
                        "settings.import_invalid",
                        "The selected file is not readable ResoDrive settings JSON.");
                }
                var validated = ValidateSettings(imported);
                if (!validated.Succeeded || validated.Value is null)
                {
                    return Result.Failure<ManagerSettings>(
                        "settings.import_invalid",
                        validated.Error?.Message ?? "The selected settings file is invalid.");
                }
                var existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                if (!existing.Succeeded || existing.Value is null)
                {
                    return Result.Failure<ManagerSettings>(
                        existing.Error?.Code ?? "settings.load_failed",
                        existing.Error?.Message ?? "The current settings could not be loaded.");
                }
                var updated = validated.Value with
                {
                    SchemaVersion = ManagerSettings.CurrentSchemaVersion,
                    Revision = checked(existing.Value.Revision + 1)
                };
                await using (var stream = new FileStream(
                    stagedPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        updated,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(_path))
                    File.Replace(stagedPath, _path, previousPath, ignoreMetadataErrors: true);
                else
                    File.Move(stagedPath, _path);
                return Result.Success(updated);
            }
            finally
            {
                if (File.Exists(stagedPath))
                    File.Delete(stagedPath);
            }
        }
        catch (IOException exception)
        {
            return Result.Failure<ManagerSettings>("settings.import_io", exception.Message, true);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<ManagerSettings>("settings.import_access_denied", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ManagerSettings>("settings.import_invalid_path", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<OperationResult<ManagerSettings>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Result.Success(new ManagerSettings());
        }

        var primary = await TryReadAsync(_path, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            var validated = ValidateSettings(primary);
            if (validated.Succeeded || validated.Error?.Code == "settings.unsupported_schema")
            {
                return validated;
            }
        }

        var backupPath = _path + ".bak";
        var backup = File.Exists(backupPath)
            ? await TryReadAsync(backupPath, cancellationToken).ConfigureAwait(false)
            : null;
        if (backup is not null)
        {
            var validatedBackup = ValidateSettings(backup);
            if (validatedBackup.Succeeded || validatedBackup.Error?.Code == "settings.unsupported_schema")
            {
                return validatedBackup;
            }
        }
        return Result.Failure<ManagerSettings>(
            "settings.corrupt",
            "Both settings.json and its backup are unreadable or invalid. Automatic mount and sync are disabled.");
    }

    private static OperationResult<ManagerSettings> ValidateSettings(ManagerSettings settings)
    {
        if (settings.SchemaVersion != ManagerSettings.CurrentSchemaVersion)
        {
            return Result.Failure<ManagerSettings>(
                "settings.unsupported_schema",
                $"Settings schema {settings.SchemaVersion} is not supported by this application version.");
        }
        if (settings.Application is null || settings.Mounts is null)
        {
            return Result.Failure<ManagerSettings>(
                "settings.invalid",
                "The settings document is incomplete.");
        }

        var definitions = new List<MountDefinition>();
        foreach (var mount in settings.Mounts)
        {
            if (mount is null)
            {
                return Result.Failure<ManagerSettings>(
                    "settings.invalid",
                    "The settings document contains an empty drive entry.");
            }
            var mapped = MountDefinitionMapper.ToDomain(mount);
            if (!mapped.Succeeded || mapped.Value is null)
            {
                return Result.Failure<ManagerSettings>(
                    "settings.invalid",
                    mapped.Error?.Message ?? "The settings document contains an invalid drive.");
            }
            definitions.Add(mapped.Value);
        }

        var catalog = new MountDefinitionValidator().ValidateCatalog(definitions);
        return catalog.IsValid
            ? Result.Success(settings)
            : Result.Failure<ManagerSettings>("settings.invalid", catalog.Issues[0].Message);
    }

    private static async Task<ManagerSettings?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<ManagerSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task WriteAtomicallyAsync(ManagerSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = _path + ".bak";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
