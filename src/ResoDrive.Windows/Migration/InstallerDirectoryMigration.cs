using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ResoDrive.Windows;

/// <summary>MSI-owned staging, rollback and temporary legacy-launcher cleanup. No user data is deleted here.</summary>
public static class InstallerDirectoryMigration
{
    private sealed record TaskSnapshot(string Name, string Xml, bool Enabled);
    private sealed record ProfileSnapshot(string UserId, string LegacyRoot, string TaskName, StartupTaskRecord? PreviousTask);
    private sealed record Plan(Guid Id, string Phase, string? BridgeHash, string? ProfileHash, TaskSnapshot[] Tasks, ProfileSnapshot[] Profiles);
    private const string CleanupTask = "ResoDrive Installation Migration Cleanup";
    private const string DataTaskDescription = "Migrates this user's ResoDrive data folder once, without enabling application startup.";
    private static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ResoDriveMigration");
    private static string PlanPath => Path.Combine(StateDirectory, "installation.json");
    private static string ProfilePath => Path.Combine(StateDirectory, "profiles.json");

    public static void RecordFailure(string action, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            EnsureProtectedDirectory();
            File.WriteAllText(Path.Combine(StateDirectory, "failure.json"), JsonSerializer.Serialize(new {
                Action = action, Message = RecoveryToolsService.Sanitize(exception.Message), RecordedAtUtc = DateTimeOffset.UtcNow }));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    public static void Stage(string previousDirectory)
    {
        // AppSearch captures this location from the old MSI component
        // before native removal. Deferred staging runs elevated afterwards;
        // unmanaged deployment profiles and startup tasks survive that removal.
        if (string.IsNullOrWhiteSpace(previousDirectory) || !Path.IsPathFullyQualified(previousDirectory))
            throw new IOException("The previous MSI installation directory could not be discovered.");
        if (!InstallationDirectories.SamePath(previousDirectory, InstallationDirectories.Legacy)) return;
        EnsureProtectedDirectory();
        using var lease = Lease();
        var previous = ReadPlan();
        if (previous is { Phase: not ("Complete" or "Staged") }) throw new IOException("An earlier installation migration is still pending. Finish it before upgrading again.");
        var tasks = new List<TaskSnapshot>();
        foreach (var (name, record) in ComStartupTaskStore.Enumerate())
        {
            if (!name.StartsWith(ScheduledTaskAutostartService.TaskNamePrefix + " - ", StringComparison.Ordinal)) continue;
            var user = ScheduledTaskDefinition.UserId(record.Xml);
            if (user is not null && name == ScheduledTaskAutostartService.TaskNameForUser(user) &&
                ScheduledTaskDefinition.IsOwned(record.Xml, InstallationDirectories.LegacyExecutable, user))
                tasks.Add(new(name, record.Xml, record.Enabled));
        }
        var profile = Path.Combine(InstallationDirectories.Legacy, "profiles.json");
        string? profileHash = null;
        RejectLinks(profile);
        if (File.Exists(profile))
        {
            profileHash = Hash(profile);
            File.Copy(profile, ProfilePath, overwrite: true);
            if (Hash(ProfilePath) != profileHash) throw new IOException("The deployment profile changed during migration staging.");
        }
        WritePlan(new(Guid.NewGuid(), "Staged", null, profileHash, tasks.ToArray(), DiscoverProfiles()));
    }

    public static void Apply()
    {
        EnsureProtectedDirectory();
        using var lease = Lease();
        var plan = ReadPlan();
        if (plan is null || plan.Phase != "Staged") return;
        RejectLinks(InstallationDirectories.Current);
        RejectLinks(InstallationDirectories.Legacy);
        var hash = Hash(InstallationDirectories.Executable);
        if (File.Exists(InstallationDirectories.LegacyExecutable))
            throw new IOException("The previous executable is still present after MSI removal. It was left unchanged.");
        Directory.CreateDirectory(InstallationDirectories.Legacy);
        // Persist ownership before writing any temporary artifact, for rollback
        // even if this deferred custom action is interrupted halfway through.
        plan = plan with { Phase = "Applying", BridgeHash = hash };
        WritePlan(plan);
        File.Copy(InstallationDirectories.Executable, InstallationDirectories.LegacyExecutable);
        if (Hash(InstallationDirectories.LegacyExecutable) != hash) throw new IOException("The temporary update bridge failed verification.");
        if (plan.ProfileHash is not null)
        {
            var destination = Path.Combine(InstallationDirectories.Current, "profiles.json");
            RejectLinks(destination);
            if (File.Exists(destination) && Hash(destination) != plan.ProfileHash)
                throw new IOException("The new installation already has a different deployment profile. Both profiles were preserved.");
            if (Hash(ProfilePath) != plan.ProfileHash) throw new IOException("The staged deployment profile changed.");
            File.Copy(ProfilePath, destination, overwrite: true);
        }
        var store = new ComStartupTaskStore();
        foreach (var task in plan.Tasks)
        {
            var current = store.Read(task.Name);
            if (current != new StartupTaskRecord(task.Xml, task.Enabled)) throw new IOException("A startup task changed while migration was running.");
            store.Register(task.Name, ScheduledTaskDefinition.Retarget(task.Xml, InstallationDirectories.Executable));
            store.SetEnabled(task.Name, task.Enabled);
            var migrated = store.Read(task.Name);
            var user = ScheduledTaskDefinition.UserId(task.Xml)!;
            if (migrated is null || migrated.Enabled != task.Enabled ||
                !ScheduledTaskDefinition.IsOwned(migrated.Xml, InstallationDirectories.Executable, user))
                throw new IOException("A migrated startup task could not be verified.");
        }
        foreach (var profile in plan.Profiles)
        {
            if (store.Read(profile.TaskName) != profile.PreviousTask)
                throw new IOException("A user migration task changed during installation.");
            ComStartupTaskStore.RegisterForUser(profile.TaskName, UserMigrationTaskXml(profile.UserId), profile.UserId);
            var task = store.Read(profile.TaskName);
            if (task is null || !IsUserMigrationTask(task.Xml, profile.UserId))
                throw new IOException("A user migration task could not be verified.");
        }
        WritePlan(plan with { Phase = "Applied" });
    }

    public static void Rollback()
    {
        if (!Directory.Exists(StateDirectory)) return;
        EnsureProtectedDirectory();
        using var lease = Lease();
        var plan = ReadPlan();
        if (plan is null || plan.Phase is "Complete" or "Committed") return;
        var store = new ComStartupTaskStore();
        foreach (var task in plan.Tasks)
        {
            var current = store.Read(task.Name);
            var user = ScheduledTaskDefinition.UserId(task.Xml)!;
            if (current is not null && ScheduledTaskDefinition.IsOwned(current.Xml, InstallationDirectories.Executable, user))
            {
                store.Register(task.Name, task.Xml);
                store.SetEnabled(task.Name, task.Enabled);
            }
        }
        foreach (var profile in plan.Profiles)
        {
            var task = store.Read(profile.TaskName);
            if (task is null || !IsUserMigrationTask(task.Xml, profile.UserId)) continue;
            if (profile.PreviousTask is { } previous)
            {
                ComStartupTaskStore.RegisterForUser(profile.TaskName, previous.Xml, profile.UserId);
                store.SetEnabled(profile.TaskName, previous.Enabled);
            }
            else store.Delete(profile.TaskName);
        }
        // Native MSI rollback may already have restored the original executable.
        // Remove only our bridge; a restored old executable must remain in place.
        if (plan.BridgeHash is not null && File.Exists(InstallationDirectories.LegacyExecutable) &&
            Hash(InstallationDirectories.LegacyExecutable) == plan.BridgeHash)
            File.Delete(InstallationDirectories.LegacyExecutable);
        // Apply may have failed because an independent destination profile already
        // existed. It belongs to neither this transaction nor its rollback. Keep
        // that file and still finish the journal so a corrected retry is possible.
        var destinationProfile = Path.Combine(InstallationDirectories.Current, "profiles.json");
        if (plan.ProfileHash is not null && File.Exists(destinationProfile) && Hash(destinationProfile) == plan.ProfileHash)
            File.Delete(destinationProfile);
        RemoveIfEmpty(InstallationDirectories.Legacy);
        WritePlan(plan with { Phase = "Complete" });
    }

    public static void Commit()
    {
        if (!Directory.Exists(StateDirectory)) return;
        EnsureProtectedDirectory();
        using var lease = Lease();
        var plan = ReadPlan();
        if (plan is null || plan.Phase != "Applied") return;
        // A one-shot SYSTEM task retries after restart if a legacy handoff still
        // holds the bridge. It is removed after cleanup; no resident service exists.
        RegisterCleanupTask();
        WritePlan(plan with { Phase = "Committed" });
        using var cleanup = Process.Start(new ProcessStartInfo(InstallationDirectories.Executable)
        {
            Arguments = "--cleanup-install-migration", UseShellExecute = false,
            CreateNoWindow = true, WorkingDirectory = InstallationDirectories.Current,
        });
    }

    public static async Task CleanupAsync(CancellationToken token)
    {
        EnsureProtectedDirectory();
        // The old updater must be able to launch the bridge after msiexec exits.
        // Never delete it merely because no process is currently using that file.
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (LegacyHandoffRunning() || LegacyLauncherRunning())
        {
            if (DateTime.UtcNow >= deadline) return; // Durable task retries on boot.
            await Task.Delay(250, token).ConfigureAwait(false);
        }
        using var lease = Lease();
        var plan = ReadPlan();
        if (plan is null || plan.Phase != "Committed") return;
        if (!InstalledApplicationLocator.GetInstallationDirectories().Any(directory => InstallationDirectories.SamePath(directory, InstallationDirectories.Current)))
            throw new IOException("The migrated MSI installation could not be verified; cleanup was deferred.");
        DeleteMatching(InstallationDirectories.LegacyExecutable, plan.BridgeHash);
        DeleteMatching(Path.Combine(InstallationDirectories.Legacy, "profiles.json"), plan.ProfileHash);
        RemoveIfEmpty(InstallationDirectories.Legacy);
        if (Directory.Exists(InstallationDirectories.Legacy)) return; // Unknown files are never recursively deleted.
        var store = new ComStartupTaskStore();
        var pending = false;
        foreach (var profile in plan.Profiles)
        {
            if (DirectoryIsPresent(profile.LegacyRoot)) { pending = true; continue; }
            var task = store.Read(profile.TaskName);
            if (task is not null && IsUserMigrationTask(task.Xml, profile.UserId)) store.Delete(profile.TaskName);
        }
        if (pending) return; // A signed-out user's own task completes at next sign-in.
        WritePlan(plan with { Phase = "Complete" });
        DeleteCleanupTask();
        DeleteMatching(ProfilePath, plan.ProfileHash);
    }

    private static bool LegacyHandoffRunning()
    {
        var processes = Process.GetProcessesByName("resodrive-update-helper");
        try
        {
            // Waiting for unrelated helpers is harmless; unverifiable processes
            // defer cleanup rather than risking the active legacy update.
            return processes.Any(process => !process.HasExited);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool LegacyLauncherRunning()
    {
        var processes = Process.GetProcessesByName("resodrive");
        try
        {
            foreach (var process in processes)
            {
                if (process.Id == Environment.ProcessId || process.HasExited) continue;
                try
                {
                    if (process.MainModule?.FileName is not { } path || InstallationDirectories.SamePath(path, InstallationDirectories.LegacyExecutable)) return true;
                }
                catch (System.ComponentModel.Win32Exception) { return true; }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static string Hash(string path)
    {
        RejectLinks(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void DeleteMatching(string path, string? hash)
    {
        if (hash is null || !File.Exists(path)) return;
        if (Hash(path) != hash) throw new IOException("A migration artifact changed and was preserved instead of deleted.");
        File.Delete(path);
    }

    private static void RemoveIfEmpty(string path)
    {
        RejectLinks(path);
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    private static void EnsureProtectedDirectory()
    {
        RejectLinks(StateDirectory);
        if (!Directory.Exists(StateDirectory))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(StateDirectory).Create(security);
        }
        var owner = new DirectoryInfo(StateDirectory).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier identity || !(identity.IsWellKnown(WellKnownSidType.LocalSystemSid) || identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            throw new UnauthorizedAccessException("The installer migration directory is not owned by Windows or Administrators.");
        foreach (var name in new[] { "installation.json", "installation.json.tmp", "installation.lock", "profiles.json", "failure.json" }) RejectLinks(Path.Combine(StateDirectory, name));
    }

    private static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] is not ('\\' or '/') ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Installer migration requires local drive paths.");
        var parents = new Stack<string>();
        for (var current = full; current is not null; current = Path.GetDirectoryName(current)) parents.Push(current);
        foreach (var current in parents)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Installer migration cannot follow junctions or symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static FileStream Lease() => new(Path.Combine(StateDirectory, "installation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static Plan? ReadPlan()
    {
        if (!File.Exists(PlanPath)) return null;
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(PlanPath)) ?? throw new IOException("The installer migration journal is invalid.");
        if (plan.Id == Guid.Empty || plan.Phase is not ("Staged" or "Applying" or "Applied" or "Committed" or "Complete"))
            throw new IOException("The installer migration journal is invalid.");
        return plan;
    }

    private static void WritePlan(Plan plan)
    {
        using (var stream = new FileStream(PlanPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, plan);
            stream.Flush(flushToDisk: true);
        }
        File.Move(PlanPath + ".tmp", PlanPath, overwrite: true);
    }

    private static string CleanupTaskXml()
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "Finishes the ResoDrive installation-directory migration.")),
            new XElement(ns + "Triggers", new XElement(ns + "BootTrigger", new XElement(ns + "Enabled", true)),
                new XElement(ns + "LogonTrigger", new XElement(ns + "Enabled", true), new XElement(ns + "Delay", "PT1M"))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "System"),
                new XElement(ns + "UserId", "S-1-5-18"), new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", new XElement(ns + "ExecutionTimeLimit", "PT10M"), new XElement(ns + "StartWhenAvailable", true)),
            new XElement(ns + "Actions", new XAttribute("Context", "System"), new XElement(ns + "Exec",
                new XElement(ns + "Command", InstallationDirectories.Executable), new XElement(ns + "Arguments", "--cleanup-install-migration")))).ToString();
    }

    private static ProfileSnapshot[] DiscoverProfiles()
    {
        var result = new List<ProfileSnapshot>();
        var store = new ComStartupTaskStore();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
            ?? throw new IOException("Windows user profiles could not be enumerated.");
        foreach (var userId in profiles.GetSubKeyNames())
        {
            if (!(userId.StartsWith("S-1-5-21-", StringComparison.Ordinal) || userId.StartsWith("S-1-12-1-", StringComparison.Ordinal))) continue;
            using var profile = profiles.OpenSubKey(userId);
            if (profile?.GetValue("ProfileImagePath") is not string directory) continue;
            directory = Environment.ExpandEnvironmentVariables(directory);
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("A Windows profile has an unsupported data location.");
            var local = Path.Combine(directory, "AppData", "Local");
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var folders = users.OpenSubKey(userId + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (folders?.GetValue("Local AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string configured)
                local = Environment.ExpandEnvironmentVariables(configured.Replace("%USERPROFILE%", directory, StringComparison.OrdinalIgnoreCase));
            var root = Path.Combine(local, ApplicationPaths.LegacyDataDirectoryName);
            if (!DirectoryIsPresent(root)) continue;
            var taskName = "ResoDrive Data Migration - " + ScheduledTaskAutostartService.TaskNameForUser(userId).Split(" - ", StringSplitOptions.None)[^1];
            var previous = store.Read(taskName);
            if (previous is not null && !IsUserMigrationTask(previous.Xml, userId))
                throw new IOException("Another task uses a user migration name and was left unchanged.");
            result.Add(new(userId, root, taskName, previous));
        }
        return result.ToArray();
    }

    private static bool DirectoryIsPresent(string path)
    {
        RejectLinks(path);
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string UserMigrationTaskXml(string userId)
    {
        var document = XDocument.Parse(ScheduledTaskDefinition.CreateXml(InstallationDirectories.Executable, userId));
        var root = document.Root!;
        var ns = root.Name.Namespace;
        root.Element(ns + "RegistrationInfo")!.Element(ns + "Description")!.Value = DataTaskDescription;
        root.Element(ns + "RegistrationInfo")!.Element(ns + "URI")!.Remove();
        root.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Arguments")!.Value = "--migrate-user-data";
        root.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!.Add(new XElement(ns + "Delay", "PT20S"));
        return document.ToString();
    }

    private static bool IsUserMigrationTask(string xml, string userId)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root!;
        var ns = root.Name.Namespace;
        var description = root.Element(ns + "RegistrationInfo")?.Element(ns + "Description");
        var arguments = root.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Arguments");
        if (description?.Value != DataTaskDescription || arguments?.Value != "--migrate-user-data") return false;
        description.Value = ScheduledTaskAutostartService.TaskDescription;
        arguments.Value = AutostartCommand.BackgroundArgument;
        return ScheduledTaskDefinition.IsOwned(document.ToString(), InstallationDirectories.Executable, userId);
    }

    private static void RegisterCleanupTask() => WithTaskFolder(folder =>
    {
        object? previous = null;
        object? task = null;
        try
        {
            try { previous = folder.GetTask(CleanupTask); }
            catch (Exception exception) when (ComStartupTaskStore.NotFound(exception)) { }
            if (previous is not null && !IsCleanupTask((string)((dynamic)previous).Xml))
                throw new IOException("A different task uses the migration cleanup name and was left unchanged.");
            task = folder.RegisterTask(CleanupTask, CleanupTaskXml(), 6, "SYSTEM", null, 5, null);
        }
        finally { Release(task); Release(previous); }
    });

    private static void DeleteCleanupTask() => WithTaskFolder(folder =>
    {
        object? task = null;
        try
        {
            try { task = folder.GetTask(CleanupTask); }
            catch (Exception exception) when (ComStartupTaskStore.NotFound(exception)) { return; }
            if (IsCleanupTask((string)((dynamic)task).Xml)) folder.DeleteTask(CleanupTask, 0);
        }
        finally { Release(task); }
    });

    private static bool IsCleanupTask(string xml)
    {
        var root = XDocument.Parse(xml).Root!;
        var ns = root.Name.Namespace;
        var actions = root.Element(ns + "Actions")?.Elements().ToArray() ?? [];
        var principal = root.Element(ns + "Principals")?.Element(ns + "Principal");
        return actions.Length == 1 && actions[0].Name == ns + "Exec" &&
            ScheduledTaskDefinition.UserId(xml) == "S-1-5-18" &&
            root.Element(ns + "RegistrationInfo")?.Element(ns + "Description")?.Value == "Finishes the ResoDrive installation-directory migration." &&
            actions[0].Element(ns + "Command")?.Value == InstallationDirectories.Executable &&
            actions[0].Element(ns + "Arguments")?.Value == "--cleanup-install-migration";
    }

    private static void WithTaskFolder(Action<dynamic> action)
    {
        object? service = null;
        object? folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            action((dynamic)folder);
        }
        finally { Release(folder); Release(service); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value);
    }
}
