using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ResoDrive.Windows;

/// <summary>
/// Bounded local diagnostics. Writes are best effort, sanitized, and serialized with
/// account cleanup. Raw messages and stacks stay local; export allowlisted summaries.
/// This is not a crash dump writer and must not attempt to recover corrupted processes.
/// </summary>
public sealed partial class ProcessDiagnosticLog
{
    public const long DefaultMaximumBytes = 512 * 1024;
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMilliseconds(150);
    private readonly object _gate = new();
    private readonly ApplicationPaths? _paths;
    private readonly string _directory;
    private readonly bool _requiresGuard;
    private readonly bool _localDestination;
    private readonly AccountDataGuard? _accountData;
    private readonly long _maximumBytes;

    public ProcessDiagnosticLog(ApplicationPaths paths, string processRole,
        long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (processRole is not ("ui" or "host" or "helper"))
            throw new ArgumentException("An established process role is required.", nameof(processRole));
        if (maximumBytes < 100 || maximumBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _paths = paths;
        _directory = paths.Logs;
        _requiresGuard = true;
        _maximumBytes = maximumBytes;
        LogFile = Path.Combine(paths.Logs, $"resodrive-{processRole}.log");
        SessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _localDestination = IsLocalDiagnosticPath(LogFile);
        // An account root may intentionally be remote. Diagnostics must never probe
        // its wipe marker or filesystem: offline network I/O has no bounded timeout.
        if (!_localDestination) return;
        try
        {
            RejectRedirectedPaths();
            _accountData = new AccountDataGuard(paths);
        }
        catch (Exception exception) when (IsDiagnosticIoFailure(exception))
        {
            // An unreadable wipe marker blocks all account writes, including diagnostics.
            // Never fall back to an unguarded writer, even to report that read failure.
        }
    }

    /// <summary>Injected log destination; callers must provide the active account guard for account data.</summary>
    public ProcessDiagnosticLog(string path, long maximumBytes = DefaultMaximumBytes,
        AccountDataGuard? accountData = null)
    {
        if (maximumBytes < 100 || maximumBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        LogFile = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(LogFile)!;
        _accountData = accountData;
        _requiresGuard = accountData is not null;
        _maximumBytes = maximumBytes;
        SessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _localDestination = IsLocalDiagnosticPath(LogFile);
    }

    public string LogFile { get; }
    public string SessionId { get; }

    public void StartSession(string? applicationVersion = null) => WriteCore("INFO", "process.started",
        $"version={DiagnosticIdentity.VersionText(applicationVersion ?? DiagnosticIdentity.ApplicationVersion)} " +
        $"commit={DiagnosticIdentity.CommitText(applicationVersion ?? DiagnosticIdentity.ApplicationVersion)} " +
        $"runtime={DiagnosticIdentity.RuntimeText} " +
        $"processArchitecture={DiagnosticIdentity.ProcessArchitecture} " +
        $"osArchitecture={DiagnosticIdentity.OperatingSystemArchitecture} " +
        $"windows={Environment.OSVersion.Version}", null, sanitize: false);

    public bool Information(string eventName, string? detail = null) =>
        Write("INFO", eventName, detail, errorId: null);

    public string Exception(string eventName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var errorId = NewErrorId();
        // Exception.ToString can be overridden by third-party exceptions. Reporting must
        // not replace the original failure if that implementation itself throws.
        string detail;
#pragma warning disable CA1031 // A diagnostic formatter must never mask the original exception.
        try { detail = exception.ToString(); }
        catch (Exception) { detail = "Exception details could not be formatted."; }
#pragma warning restore CA1031
        // Preserve CLR type/method identities separately: free-text host-name redaction
        // would otherwise mistake System.SomeException and namespace names for DNS.
        try
        {
            var metadata = $"exceptionType={Identifier(exception.GetType().FullName)} hresult=0x{exception.HResult:X8}";
            var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
            foreach (var frame in frames.Take(32))
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                metadata += $" frame={Identifier(method.DeclaringType?.FullName)}.{Identifier(method.Name)}+IL{frame.GetILOffset()}";
            }
            var bounded = detail.Length > 16 * 1024 ? detail[..(16 * 1024)] : detail;
            WriteCore("ERROR", eventName, metadata + " message=" + RecoveryToolsService.Sanitize(bounded), errorId, sanitize: false);
        }
#pragma warning disable CA1031 // Reflection/redaction failure must not mask the exception being recorded.
        catch (Exception) { WriteCore("ERROR", eventName, "Exception details could not be collected.", errorId, sanitize: false); }
#pragma warning restore CA1031
        return errorId;
    }

    public bool Write(string level, string eventName, string? detail = null, string? errorId = null)
        => WriteCore(level, eventName, detail, errorId, sanitize: true);

    private bool WriteCore(string level, string eventName, string? detail, string? errorId, bool sanitize)
    {
        // Recheck before the guard touches disk: a drive letter can be remapped
        // after construction. Do not use IsReady or enumerate remote files.
        if (!_localDestination || !IsLocalDiagnosticPath(LogFile) ||
            _requiresGuard && _accountData is null) return false;
        var entered = false;
        try
        {
            RejectRedirectedPaths();
            if (_accountData?.IsBlocked == true) return false;
            entered = Monitor.TryEnter(_gate, LeaseTimeout);
            if (!entered) return false;
            RejectRedirectedPaths();
            using var timeout = new CancellationTokenSource(LeaseTimeout);
            using var lease = _accountData?.AcquireAsync(timeout.Token).GetAwaiter().GetResult();
            RejectRedirectedPaths();

            var safeLevel = level is "INFO" or "WARN" or "ERROR" or "CRITICAL" ? level : "INFO";
            var safeEvent = eventName is { Length: <= 96 } && EventPattern().IsMatch(eventName)
                ? eventName : "diagnostic.event";
            var id = errorId is not null && ErrorIdPattern().IsMatch(errorId) ? $" errorId={errorId}" : string.Empty;
            var line = $"{DateTimeOffset.UtcNow:O} level={safeLevel} event={safeEvent}{id} " +
                $"pid={Environment.ProcessId} session={SessionId}";
            if (!string.IsNullOrWhiteSpace(detail))
            {
                // Bound regex processing and disk space even when an exception contains
                // a huge payload. Sanitization is deliberately local-only best effort.
                var bounded = detail.Length > 16 * 1024 ? detail[..(16 * 1024)] : detail;
                line += " detail=\"" + OneLine(sanitize ? RecoveryToolsService.Sanitize(bounded) : bounded) + "\"";
            }
            line = FitLine(line);
            var bytes = Encoding.UTF8.GetBytes(line);
            Directory.CreateDirectory(_directory);
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length + bytes.Length > _maximumBytes)
                File.Move(LogFile, LogFile + ".1", overwrite: true);
            using var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: safeLevel is "ERROR" or "CRITICAL");
            return true;
        }
        catch (Exception exception) when (IsDiagnosticIoFailure(exception)) { return false; }
        finally { if (entered) Monitor.Exit(_gate); }
    }

    private string FitLine(string line)
    {
        var maximum = (int)Math.Min(_maximumBytes, 32 * 1024);
        // UTF-8 uses at most three bytes per UTF-16 code unit; cap once, then trim
        // the small remaining excess without splitting surrogate pairs.
        if (line.Length > maximum) line = line[..maximum];
        while (Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length > maximum)
            line = line[..^1];
        if (line.Length > 0 && char.IsHighSurrogate(line[^1])) line = line[..^1];
        return line + Environment.NewLine;
    }

    private void RejectRedirectedPaths()
    {
        // Inspect ancestors in order before any access below them. A local drive
        // may contain a junction to an offline share; checking only the leaf would
        // follow that junction before discovering it.
        var ancestors = new Stack<string>();
        for (string? path = _directory; path is not null; path = Path.GetDirectoryName(path))
            ancestors.Push(path);
        foreach (var ancestor in ancestors)
        {
            try
            {
                var attributes = File.GetAttributes(ancestor);
                if ((attributes & FileAttributes.Directory) == 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Diagnostic directory ancestors must not redirect account writes.");
            }
            // Missing descendants are expected before the first write. Still inspect
            // independent account files, especially an existing account-lock redirect.
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
        var candidates = _paths is null ? [_directory, LogFile, LogFile + ".1"] :
            new[] { _paths.Root, _directory, LogFile, LogFile + ".1", Path.Combine(_paths.Root, ".account-data.lock") };
        foreach (var path in candidates)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (path != _paths?.Root && path != _directory && (attributes & FileAttributes.Directory) != 0))
                    throw new IOException("Diagnostic paths must not redirect account writes.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool IsDiagnosticIoFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OperationCanceledException or System.Security.SecurityException;

    private static bool IsLocalDiagnosticPath(string path)
    {
        // Only ordinary absolute drive-letter paths. This rejects UNC, extended
        // UNC and device namespaces before any native drive or filesystem query.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/')) return false;
        try
        {
            return new DriveInfo(path[..3]).DriveType is
                DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;
        }
        catch (Exception exception) when (IsDiagnosticIoFailure(exception)) { return false; }
    }

    private static string NewErrorId() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8].ToUpperInvariant();

    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();

    private static string Identifier(string? value) =>
        value is { Length: <= 256 } && IdentifierPattern().IsMatch(value) ? value : "Unknown";

    [GeneratedRegex(@"\A[a-z][a-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex EventPattern();
    [GeneratedRegex(@"\A[A-F0-9]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorIdPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9_.$+<>`]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
