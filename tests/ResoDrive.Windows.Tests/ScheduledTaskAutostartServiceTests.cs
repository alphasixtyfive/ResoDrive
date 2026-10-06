using System.Security.Principal;
using System.Xml.Linq;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class ScheduledTaskAutostartServiceTests
{
    private const string UserId = "S-1-5-21-1000";
    private readonly string _applicationPath = Path.GetFullPath(@"C:\Program Files\rdrive\resodrive.exe");

    [Fact]
    public void Definition_UsesImmediateNonElevatedInteractiveLogonTrigger()
    {
        var document = XDocument.Parse(ScheduledTaskDefinition.CreateXml(_applicationPath, UserId));
        var root = Assert.IsType<XElement>(document.Root);
        var ns = root.Name.Namespace;

        var trigger = Assert.Single(root.Element(ns + "Triggers")!.Elements(ns + "LogonTrigger"));
        Assert.Equal(UserId, trigger.Element(ns + "UserId")?.Value);
        Assert.Null(trigger.Element(ns + "Delay"));

        var principal = Assert.Single(root.Element(ns + "Principals")!.Elements(ns + "Principal"));
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")?.Value);
        Assert.Equal("LeastPrivilege", principal.Element(ns + "RunLevel")?.Value);

        var settings = root.Element(ns + "Settings")!;
        Assert.Equal("false", settings.Element(ns + "RunOnlyIfNetworkAvailable")?.Value);
        Assert.Equal("PT0S", settings.Element(ns + "ExecutionTimeLimit")?.Value);

        var action = Assert.Single(root.Element(ns + "Actions")!.Elements(ns + "Exec"));
        Assert.Equal(_applicationPath, action.Element(ns + "Command")?.Value);
        Assert.Equal("--background", action.Element(ns + "Arguments")?.Value);
        Assert.Equal(
            $"\\{ScheduledTaskAutostartService.TaskNameForUser(UserId)}",
            root.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value);
    }

    [Fact]
    public void TaskName_IsStableAndUniquePerUser()
    {
        var first = ScheduledTaskAutostartService.TaskNameForUser(UserId);

        Assert.Equal(first, ScheduledTaskAutostartService.TaskNameForUser(UserId));
        Assert.NotEqual(first, ScheduledTaskAutostartService.TaskNameForUser("S-1-5-21-2000"));
        Assert.StartsWith("ResoDrive Startup - ", first, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTask_RecognizesWindowsExceptionVariants()
    {
        Assert.True(ComStartupTaskStore.NotFound(new FileNotFoundException()));
        Assert.False(ComStartupTaskStore.NotFound(new UnauthorizedAccessException()));
    }

    [Fact]
    public void Definition_OwnershipRejectsChangedExecutable()
    {
        var xml = ScheduledTaskDefinition.CreateXml(_applicationPath, UserId);

        Assert.True(ScheduledTaskDefinition.IsOwned(xml, _applicationPath, UserId));
        Assert.False(ScheduledTaskDefinition.IsOwned(xml, @"C:\Other\resodrive.exe", UserId));
        Assert.False(ScheduledTaskDefinition.IsOwned(xml, _applicationPath, "S-1-5-21-2000"));
    }

    [Fact]
    public void Definition_OwnershipRejectsAdditionalAction()
    {
        var document = XDocument.Parse(ScheduledTaskDefinition.CreateXml(_applicationPath, UserId));
        var root = document.Root!;
        var ns = root.Name.Namespace;
        root.Element(ns + "Actions")!.Add(
            new XElement(ns + "Exec", new XElement(ns + "Command", "malicious.exe")));

        Assert.False(ScheduledTaskDefinition.IsOwned(document.ToString(), _applicationPath, UserId));
    }

    [Fact]
    public void Definition_OwnershipAcceptsOnlyTheFixedAdjacentLauncher()
    {
        var launcher = Path.Combine(Path.GetDirectoryName(_applicationPath)!, ApplicationLauncher.FileName);
        Assert.True(ScheduledTaskDefinition.IsOwned(ScheduledTaskDefinition.CreateXml(launcher, UserId), _applicationPath, UserId));
        Assert.False(ScheduledTaskDefinition.IsOwned(ScheduledTaskDefinition.CreateXml(@"C:\Other\resodrive-launcher.exe", UserId), _applicationPath, UserId));
        Assert.False(ScheduledTaskDefinition.IsOwned(ScheduledTaskDefinition.CreateXml(launcher, "S-1-5-21-2000"), _applicationPath, UserId));

        var document = XDocument.Parse(ScheduledTaskDefinition.CreateXml(launcher, UserId));
        var ns = document.Root!.Name.Namespace;
        document.Root.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "RunLevel")!.Value = "HighestAvailable";
        Assert.False(ScheduledTaskDefinition.IsOwned(document.ToString(), _applicationPath, UserId));
    }

    [Fact]
    public void Definition_OwnershipAcceptsTaskSchedulerNormalizedIdentityAndRunLevel()
    {
        var identity = WindowsIdentity.GetCurrent();
        var sid = Assert.IsType<SecurityIdentifier>(identity.User).Value;
        var account = Assert.IsType<NTAccount>(
            identity.User.Translate(typeof(NTAccount))).Value;
        var document = XDocument.Parse(ScheduledTaskDefinition.CreateXml(_applicationPath, sid));
        var root = document.Root!;
        var ns = root.Name.Namespace;
        root.Element(ns + "Triggers")!
            .Element(ns + "LogonTrigger")!
            .Element(ns + "UserId")!.Value = account;
        root.Element(ns + "Principals")!
            .Element(ns + "Principal")!
            .Element(ns + "RunLevel")!.Remove();

        Assert.True(ScheduledTaskDefinition.IsOwned(document.ToString(), _applicationPath, sid));
    }

    [Fact]
    public async Task Enable_RegistersTask()
    {
        var tasks = new FakeTaskStore();
        var service = CreateService(tasks);

        var result = await service.SetEnabledAsync(true);

        Assert.True(result.Succeeded);
        Assert.NotNull(tasks.Record);
        Assert.True(tasks.Record!.Enabled);
    }

    [Fact]
    public async Task Disable_RemovesTask()
    {
        var tasks = new FakeTaskStore
        {
            Record = new StartupTaskRecord(
                ScheduledTaskDefinition.CreateXml(_applicationPath, UserId),
                true)
        };
        var service = CreateService(tasks);

        var result = await service.SetEnabledAsync(false);

        Assert.True(result.Succeeded);
        Assert.Null(tasks.Record);
    }

    [Fact]
    public async Task Enable_DoesNotReplaceForeignTask()
    {
        var tasks = new FakeTaskStore
        {
            Record = new StartupTaskRecord(
                ScheduledTaskDefinition.CreateXml(@"C:\Other\resodrive.exe", UserId),
                true)
        };
        var service = CreateService(tasks);

        var result = await service.SetEnabledAsync(true);

        Assert.False(result.Succeeded);
        Assert.Equal("autostart.foreign_task", result.Error?.Code);
        Assert.Contains(@"C:\Other\resodrive.exe", tasks.Record!.Xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnableUsesLauncherAndReconciliationDoesNotRewriteCorrectTask()
    {
        using var installation = new DisposableInstallation();
        var tasks = new FakeTaskStore();
        var service = new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks);
        Assert.True((await service.SetEnabledAsync(true)).Succeeded);
        Assert.True(ScheduledTaskDefinition.UsesCommand(tasks.Record!.Xml, installation.LauncherPath));
        Assert.True((await service.ReconcileAsync(true)).Succeeded);
        Assert.Equal(1, tasks.RegisterCount);
        Assert.True((await service.IsEnabledAsync()).Value);
    }

    [Fact]
    public async Task ReconciliationMigratesAnOwnedLegacyTaskOnce()
    {
        using var installation = new DisposableInstallation();
        var tasks = new FakeTaskStore
        {
            Record = new(ScheduledTaskDefinition.CreateXml(installation.ApplicationPath, UserId), true)
        };
        var service = new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks);
        Assert.True((await service.ReconcileAsync(true)).Succeeded);
        Assert.True(ScheduledTaskDefinition.UsesCommand(tasks.Record!.Xml, installation.LauncherPath));
        Assert.True((await service.ReconcileAsync(true)).Succeeded);
        Assert.Equal(1, tasks.RegisterCount);
    }

    [Fact]
    public async Task ReconciliationMigratesDisabledTaskWithoutEnablingIt()
    {
        using var installation = new DisposableInstallation();
        var tasks = new FakeTaskStore
        {
            Record = new(ScheduledTaskDefinition.CreateXml(installation.ApplicationPath, UserId), false)
        };
        var service = new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks);
        Assert.True((await service.ReconcileAsync(false)).Succeeded);
        Assert.False(tasks.Record!.Enabled);
        Assert.True(ScheduledTaskDefinition.UsesCommand(tasks.Record.Xml, installation.LauncherPath));
        Assert.True((await service.ReconcileAsync(false)).Succeeded);
        Assert.Equal(1, tasks.RegisterCount);
        Assert.Equal(0, tasks.DeleteCount);
    }

    [Fact]
    public async Task ReconciliationLeavesForeignLauncherTaskUnchanged()
    {
        using var installation = new DisposableInstallation();
        var original = new StartupTaskRecord(ScheduledTaskDefinition.CreateXml(@"C:\Other\resodrive-launcher.exe", UserId), true);
        var tasks = new FakeTaskStore { Record = original };
        var result = await new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks).ReconcileAsync(true);
        Assert.False(result.Succeeded);
        Assert.Equal("autostart.foreign_task", result.Error?.Code);
        Assert.Equal(original, tasks.Record);
        Assert.Equal(0, tasks.RegisterCount);
        Assert.Equal(0, tasks.DeleteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedInstallerReceiptMigratesOldDirectoryTaskOnceAndPreservesPreference(bool enabled)
    {
        using var installation = new DisposableInstallation();
        var oldExecutable = @"C:\Program Files\rdrive\resodrive.exe";
        var tasks = new FakeTaskStore { Record = new(ScheduledTaskDefinition.CreateXml(oldExecutable, UserId), enabled) };
        var service = new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks,
            current => current == installation.ApplicationPath ? oldExecutable : null);
        Assert.True((await service.ReconcileAsync(enabled)).Succeeded);
        Assert.True(ScheduledTaskDefinition.UsesCommand(tasks.Record!.Xml, installation.LauncherPath));
        Assert.Equal(enabled, tasks.Record.Enabled);
        Assert.True((await service.ReconcileAsync(enabled)).Succeeded);
        Assert.Equal(1, tasks.RegisterCount);
    }

    [Fact]
    public async Task InstallerReceiptCannotAdoptTaskForAnotherAccountOrInstallation()
    {
        using var installation = new DisposableInstallation();
        var oldExecutable = @"C:\Program Files\rdrive\resodrive.exe";
        var foreign = new StartupTaskRecord(ScheduledTaskDefinition.CreateXml(oldExecutable, "S-1-5-21-2000"), true);
        var tasks = new FakeTaskStore { Record = foreign };
        var service = new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks, _ => oldExecutable);
        Assert.False((await service.ReconcileAsync(true)).Succeeded);
        Assert.Equal(foreign, tasks.Record);
        tasks.Record = new(ScheduledTaskDefinition.CreateXml(@"D:\portable\resodrive.exe", UserId), true);
        Assert.False((await service.ReconcileAsync(true)).Succeeded);
        Assert.Equal(0, tasks.RegisterCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReceiptBackedMigrationRestoresTheExactOldDirectoryTask(bool enabled)
    {
        using var installation = new DisposableInstallation();
        var oldExecutable = @"C:\Program Files\rdrive\resodrive.exe";
        var previous = new StartupTaskRecord(ScheduledTaskDefinition.CreateXml(oldExecutable, UserId), enabled);
        var tasks = new FakeTaskStore { Record = previous, ThrowVerificationRead = true };
        var service = new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks, _ => oldExecutable);
        Assert.False((await service.ReconcileAsync(enabled)).Succeeded);
        Assert.Equal(previous, tasks.Record);
    }

    [Theory]
    [InlineData("family")]
    [InlineData("registered-path")]
    [InlineData("receipt-path")]
    [InlineData("relative-legacy")]
    [InlineData("network-legacy")]
    [InlineData("same-directory")]
    public void MigrationReceiptMustAgreeWithTheRegisteredCurrentInstallation(string invalidField)
    {
        var current = @"C:\Program Files\ResoDrive\resodrive.exe";
        var registration = invalidField == "registered-path" ? @"D:\portable\resodrive.exe" : current;
        var family = invalidField == "family" ? Guid.NewGuid().ToString() : InstalledApplicationLocator.UpgradeCode;
        var receipt = invalidField == "receipt-path" ? @"D:\Other" : @"C:\Program Files\ResoDrive";
        var legacy = invalidField switch
        {
            "relative-legacy" => "rdrive",
            "network-legacy" => @"\\server\share\rdrive",
            "same-directory" => @"C:\Program Files\ResoDrive",
            _ => @"C:\Program Files\rdrive"
        };
        Assert.Null(ScheduledTaskAutostartService.PreviousExecutableFromReceipt(current, registration, family, receipt, legacy));
    }

    [Fact]
    public void ValidMigrationReceiptBindsOnlyTheRecordedPreviousExecutable()
    {
        var current = @"C:\Program Files\ResoDrive\resodrive.exe";
        Assert.Equal(@"C:\Program Files\rdrive\resodrive.exe", ScheduledTaskAutostartService.PreviousExecutableFromReceipt(
            current, current, InstalledApplicationLocator.UpgradeCode, @"C:\Program Files\ResoDrive\", @"C:\Program Files\rdrive\"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLauncherMigrationRestoresTheOriginalTaskAndEnabledState(bool enabled)
    {
        using var installation = new DisposableInstallation();
        var previous = new StartupTaskRecord(ScheduledTaskDefinition.CreateXml(installation.ApplicationPath, UserId), enabled);
        var tasks = new FakeTaskStore { Record = previous, ThrowVerificationRead = true };
        var result = await new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks).ReconcileAsync(enabled);
        Assert.False(result.Succeeded);
        Assert.Equal(previous, tasks.Record);
    }

    [Fact]
    public async Task MissingLauncherKeepsLegacyTaskAndDisabledMissingTaskNeedsNoWrite()
    {
        using var installation = new DisposableInstallation(includeLauncher: false);
        var tasks = new FakeTaskStore
        {
            Record = new(ScheduledTaskDefinition.CreateXml(installation.ApplicationPath, UserId), true)
        };
        var service = new ScheduledTaskAutostartService(installation.ApplicationPath, UserId, tasks);
        Assert.True((await service.ReconcileAsync(true)).Succeeded);
        Assert.Equal(0, tasks.RegisterCount);
        tasks.Record = null;
        Assert.True((await service.ReconcileAsync(false)).Succeeded);
        Assert.Equal(0, tasks.RegisterCount);
        Assert.Equal(0, tasks.DeleteCount);
    }

    private ScheduledTaskAutostartService CreateService(IStartupTaskStore tasks) =>
        new(_applicationPath, UserId, tasks);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationExceptionRestoresThePreviousTask(bool existing)
    {
        var previous = existing ? new StartupTaskRecord(ScheduledTaskDefinition.CreateXml(_applicationPath, UserId), false) : null;
        var tasks = new FakeTaskStore { Record = previous, ThrowVerificationRead = true };

        var result = await CreateService(tasks).SetEnabledAsync(true);

        Assert.False(result.Succeeded);
        Assert.Equal(previous, tasks.Record);
    }

    private sealed class FakeTaskStore : IStartupTaskStore
    {
        public StartupTaskRecord? Record { get; set; }
        public bool ThrowVerificationRead { get; set; }
        public int RegisterCount { get; private set; }
        public int DeleteCount { get; private set; }
        private bool _registered;

        public StartupTaskRecord? Read(string taskName)
        {
            if (_registered && ThrowVerificationRead)
            {
                ThrowVerificationRead = false;
                throw new IOException("Task Scheduler verification failed.");
            }
            return Record;
        }

        public void Register(string taskName, string xml)
        {
            _registered = true;
            RegisterCount++;
            var document = XDocument.Parse(xml);
            var root = document.Root!;
            Record = new(xml, root.Element(root.Name.Namespace + "Settings")!.Element(root.Name.Namespace + "Enabled")!.Value == "true");
        }

        public void SetEnabled(string taskName, bool enabled)
        {
            if (Record is not null)
                Record = Record with { Enabled = enabled };
        }

        public void Delete(string taskName)
        {
            DeleteCount++;
            Record = null;
        }
    }

    private sealed class DisposableInstallation : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-autostart-tests", Guid.NewGuid().ToString("N"));
        public DisposableInstallation(bool includeLauncher = true)
        {
            Directory.CreateDirectory(_root);
            ApplicationPath = Path.Combine(_root, "resodrive.exe");
            LauncherPath = Path.Combine(_root, ApplicationLauncher.FileName);
            File.WriteAllText(ApplicationPath, "disposable app fixture");
            if (includeLauncher) File.WriteAllText(LauncherPath, "disposable launcher fixture");
        }
        public string ApplicationPath { get; }
        public string LauncherPath { get; }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

}
