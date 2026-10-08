using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed class ScheduledTaskAutostartService
{
    internal const string TaskNamePrefix = "ResoDrive Startup";
    internal const string TaskDescription = "Starts ResoDrive for this user at sign-in. Managed by ResoDrive.";
    private readonly string _applicationPath;
    private readonly string _taskName;
    private readonly string _userId;
    private readonly IStartupTaskStore _tasks;

    public ScheduledTaskAutostartService(string applicationPath)
        : this(
            applicationPath,
            CurrentUserId(),
            new ComStartupTaskStore())
    {
    }

    internal ScheduledTaskAutostartService(
        string applicationPath,
        string userId,
        IStartupTaskStore tasks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        _applicationPath = Path.GetFullPath(applicationPath);
        _userId = userId;
        _taskName = TaskNameForUser(userId);
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    }

    public Task<OperationResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var task = _tasks.Read(_taskName);
            if (task is null)
            {
                return Task.FromResult(Result.Success(false));
            }
            if (!ScheduledTaskDefinition.IsOwned(task.Xml, _applicationPath, _userId))
            {
                return Task.FromResult(Result.Failure<bool>(
                    "autostart.foreign_task",
                    "The ResoDrive startup task belongs to a different installation and was left unchanged."));
            }
            return Task.FromResult(Result.Success(task.Enabled));
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Task.FromResult(Result.Failure<bool>(
                "autostart.unavailable",
                $"The Windows startup task could not be read. {exception.Message}"));
        }
    }

    public OperationResult MigrateFrom(string previousApplicationPath)
    {
        StartupTaskRecord? previous = null;
        var changed = false;
        try
        {
            previous = _tasks.Read(_taskName);
            if (previous is null || ScheduledTaskDefinition.IsOwned(previous.Xml, _applicationPath, _userId)) return Result.Success();
            if (!ScheduledTaskDefinition.IsOwned(previous.Xml, previousApplicationPath, _userId))
                return Result.Failure("autostart.foreign_task", "The startup task belongs to another installation and was left unchanged.");
            var xml = ScheduledTaskDefinition.Retarget(previous.Xml, _applicationPath);
            changed = true;
            _tasks.Register(_taskName, xml);
            _tasks.SetEnabled(_taskName, previous.Enabled);
            var updated = _tasks.Read(_taskName);
            if (updated is null || updated.Enabled != previous.Enabled || !ScheduledTaskDefinition.IsOwned(updated.Xml, _applicationPath, _userId))
                throw new IOException("The migrated startup task could not be verified.");
            return Result.Success();
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (changed) RestoreTask(previous);
            return Result.Failure("autostart.migration_failed", exception.Message);
        }
    }

    public Task<OperationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupTaskRecord? existingTask = null;
        var mutationAttempted = false;
        try
        {
            existingTask = _tasks.Read(_taskName);
            if (existingTask is not null &&
                !ScheduledTaskDefinition.IsOwned(existingTask.Xml, _applicationPath, _userId))
            {
                return Task.FromResult(Result.Failure(
                    "autostart.foreign_task",
                    "The ResoDrive startup task belongs to a different installation and was left unchanged."));
            }

            if (enabled)
            {
                var xml = ScheduledTaskDefinition.CreateXml(_applicationPath, _userId);
                mutationAttempted = true;
                _tasks.Register(_taskName, xml);
                var verified = _tasks.Read(_taskName);
                if (verified is null || !verified.Enabled ||
                    !ScheduledTaskDefinition.IsOwned(verified.Xml, _applicationPath, _userId))
                {
                    RestoreTask(existingTask);
                    return Task.FromResult(Result.Failure(
                        "autostart.task_verification_failed",
                        "Windows did not preserve the ResoDrive startup task."));
                }
            }
            else
            {
                mutationAttempted = true;
                _tasks.Delete(_taskName);
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (mutationAttempted) RestoreTask(existingTask);
            return Task.FromResult(Result.Failure(
                "autostart.access_denied",
                $"The Windows startup task could not be changed. {exception.Message}"));
        }
    }

    private void RestoreTask(StartupTaskRecord? previous)
    {
        try
        {
            if (previous is null)
            {
                _tasks.Delete(_taskName);
            }
            else
            {
                _tasks.Register(_taskName, previous.Xml);
                var restored = _tasks.Read(_taskName);
                if (restored is not null && restored.Enabled != previous.Enabled)
                {
                    _tasks.SetEnabled(_taskName, previous.Enabled);
                }
            }
        }
        catch (Exception exception) when (Expected(exception))
        {
            // Preserve the original failure. The next application start reconciles an
            // enabled setting, and disabling attempts to remove the task again.
        }
    }

    private static string CurrentUserId() =>
        WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");

    internal static string TaskNameForUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var identityHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..12];
        return $"{TaskNamePrefix} - {identityHash}";
    }

    private static bool Expected(Exception exception) =>
        exception is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or System.Security.SecurityException;
}

internal sealed record StartupTaskRecord(string Xml, bool Enabled);

internal interface IStartupTaskStore
{
    StartupTaskRecord? Read(string taskName);
    void Register(string taskName, string xml);
    void SetEnabled(string taskName, bool enabled);
    void Delete(string taskName);
}

internal sealed class ComStartupTaskStore : IStartupTaskStore
{
    private const int CreateOrUpdate = 6;
    private const int InteractiveToken = 3;
    private const int TaskNotFound = unchecked((int)0x80070002);
    private const int SchedulerTaskNotFound = unchecked((int)0x8004130F);

    internal static IReadOnlyList<(string Name, StartupTaskRecord Record)> Enumerate()
    {
        object? service = null;
        object? folder = null;
        object? tasks = null;
        var result = new List<(string, StartupTaskRecord)>();
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder("\\");
            tasks = ((dynamic)folder).GetTasks(1);
            for (var index = 1; index <= (int)((dynamic)tasks).Count; index++)
            {
                object task = ((dynamic)tasks)[index];
                try { result.Add(((string)((dynamic)task).Name, new((string)((dynamic)task).Xml, (bool)((dynamic)task).Enabled))); }
                finally { Release(task); }
            }
        }
        finally { Release(tasks); Release(folder); Release(service); }
        return result;
    }

    public StartupTaskRecord? Read(string taskName)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder("\\");
            try
            {
                task = ((dynamic)folder).GetTask(taskName);
            }
            catch (Exception exception) when (NotFound(exception))
            {
                return null;
            }
            return new StartupTaskRecord((string)((dynamic)task).Xml, (bool)((dynamic)task).Enabled);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    public void Register(string taskName, string xml)
        => RegisterForUser(taskName, xml, null);

    internal static void RegisterForUser(string taskName, string xml, string? userId)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).RegisterTask(
                taskName,
                xml,
                CreateOrUpdate,
                userId,
                null,
                InteractiveToken,
                null);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    public void SetEnabled(string taskName, bool enabled)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).GetTask(taskName);
            ((dynamic)task).Enabled = enabled;
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    public void Delete(string taskName)
    {
        object? service = null;
        object? folder = null;
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder("\\");
            try
            {
                ((dynamic)folder).DeleteTask(taskName, 0);
            }
            catch (Exception exception) when (NotFound(exception))
            {
            }
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true) ??
            throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
        var service = Activator.CreateInstance(type) ??
            throw new InvalidOperationException("Windows Task Scheduler could not be started.");
        ((dynamic)service).Connect();
        return service;
    }

    internal static bool NotFound(Exception exception) =>
        exception is FileNotFoundException ||
        exception.HResult is TaskNotFound or SchedulerTaskNotFound;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}

internal static class ScheduledTaskDefinition
{
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static string Retarget(string xml, string applicationPath)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new IOException("The startup task is invalid.");
        var action = root.Element(root.Name.Namespace + "Actions")?.Element(root.Name.Namespace + "Exec")
            ?? throw new IOException("The startup task action is invalid.");
        action.Element(root.Name.Namespace + "Command")!.Value = Path.GetFullPath(applicationPath);
        action.Element(root.Name.Namespace + "WorkingDirectory")?.Remove();
        action.Add(new XElement(root.Name.Namespace + "WorkingDirectory", Path.GetDirectoryName(Path.GetFullPath(applicationPath))));
        return document.ToString();
    }

    internal static string? UserId(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var value = root?.Element(root.Name.Namespace + "Principals")?.Element(root.Name.Namespace + "Principal")?
            .Element(root.Name.Namespace + "UserId")?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.StartsWith("S-1-", StringComparison.Ordinal) ? new SecurityIdentifier(value).Value
            : ((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value;
    }

    internal static string CreateXml(string applicationPath, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var fullPath = Path.GetFullPath(applicationPath);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Namespace + "Task",
                new XAttribute("version", "1.2"),
                new XElement(Namespace + "RegistrationInfo",
                    new XElement(Namespace + "Description", ScheduledTaskAutostartService.TaskDescription),
                    new XElement(Namespace + "URI", $"\\{ScheduledTaskAutostartService.TaskNameForUser(userId)}")),
                new XElement(Namespace + "Triggers",
                    new XElement(Namespace + "LogonTrigger",
                        new XElement(Namespace + "Enabled", true),
                        new XElement(Namespace + "UserId", userId))),
                new XElement(Namespace + "Principals",
                    new XElement(Namespace + "Principal",
                        new XAttribute("id", "CurrentUser"),
                        new XElement(Namespace + "UserId", userId),
                        new XElement(Namespace + "LogonType", "InteractiveToken"),
                        new XElement(Namespace + "RunLevel", "LeastPrivilege"))),
                new XElement(Namespace + "Settings",
                    new XElement(Namespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Namespace + "DisallowStartIfOnBatteries", false),
                    new XElement(Namespace + "StopIfGoingOnBatteries", false),
                    new XElement(Namespace + "AllowHardTerminate", true),
                    new XElement(Namespace + "StartWhenAvailable", true),
                    new XElement(Namespace + "RunOnlyIfNetworkAvailable", false),
                    new XElement(Namespace + "IdleSettings",
                        new XElement(Namespace + "StopOnIdleEnd", false),
                        new XElement(Namespace + "RestartOnIdle", false)),
                    new XElement(Namespace + "AllowStartOnDemand", true),
                    new XElement(Namespace + "Enabled", true),
                    new XElement(Namespace + "Hidden", false),
                    new XElement(Namespace + "RunOnlyIfIdle", false),
                    new XElement(Namespace + "WakeToRun", false),
                    new XElement(Namespace + "ExecutionTimeLimit", "PT0S"),
                    new XElement(Namespace + "Priority", 7)),
                new XElement(Namespace + "Actions",
                    new XAttribute("Context", "CurrentUser"),
                    new XElement(Namespace + "Exec",
                        new XElement(Namespace + "Command", fullPath),
                        new XElement(Namespace + "Arguments", AutostartCommand.BackgroundArgument),
                        new XElement(Namespace + "WorkingDirectory", Path.GetDirectoryName(fullPath))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    internal static bool IsOwned(string xml, string applicationPath, string userId)
    {
        try
        {
            var task = XDocument.Parse(xml).Root;
            if (task is null)
                return false;
            var ns = task.Name.Namespace;
            var description = task.Element(ns + "RegistrationInfo")?.Element(ns + "Description")?.Value;
            var principals = task.Element(ns + "Principals")?.Elements().ToArray() ?? [];
            var triggers = task.Element(ns + "Triggers")?.Elements().ToArray() ?? [];
            var actions = task.Element(ns + "Actions")?.Elements().ToArray() ?? [];
            if (principals.Length != 1 || principals[0].Name != ns + "Principal" ||
                triggers.Length != 1 || triggers[0].Name != ns + "LogonTrigger" ||
                actions.Length != 1 || actions[0].Name != ns + "Exec")
                return false;
            var principal = principals[0];
            var trigger = triggers[0];
            var action = actions[0];
            return string.Equals(description, ScheduledTaskAutostartService.TaskDescription, StringComparison.Ordinal) &&
                IsSameUser(principal?.Element(ns + "UserId")?.Value, userId) &&
                string.Equals(principal?.Element(ns + "LogonType")?.Value, "InteractiveToken", StringComparison.Ordinal) &&
                IsLeastPrivilege(principal?.Element(ns + "RunLevel")?.Value) &&
                IsSameUser(trigger?.Element(ns + "UserId")?.Value, userId) &&
                string.Equals(action?.Element(ns + "Command")?.Value, Path.GetFullPath(applicationPath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(action?.Element(ns + "Arguments")?.Value, AutostartCommand.BackgroundArgument, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsLeastPrivilege(string? runLevel) =>
        string.IsNullOrEmpty(runLevel) ||
        string.Equals(runLevel, "LeastPrivilege", StringComparison.Ordinal);

    private static bool IsSameUser(string? candidate, string expected)
    {
        if (string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var expectedSid = new SecurityIdentifier(expected);
            var candidateSid = new NTAccount(candidate)
                .Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return candidateSid is not null && expectedSid.Equals(candidateSid);
        }
        catch (SystemException)
        {
            return false;
        }
    }
}

public static class AutostartCommand
{
    public const string BackgroundArgument = "--background";

    public static string Create(string applicationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        return $"\"{Path.GetFullPath(applicationPath)}\" {BackgroundArgument}";
    }

    public static bool IsBackgroundArgument(string? argument) =>
        string.Equals(argument, BackgroundArgument, StringComparison.OrdinalIgnoreCase);
}
