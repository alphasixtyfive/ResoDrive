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

    private ScheduledTaskAutostartService CreateService(IStartupTaskStore tasks) =>
        new(_applicationPath, UserId, tasks);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_PreservesEnabledStateAndRetargetsOwnedTask(bool enabled)
    {
        var tasks = new FakeTaskStore { Record = new(ScheduledTaskDefinition.CreateXml(_applicationPath, UserId), enabled) };
        var current = Path.GetFullPath(@"C:\Program Files\ResoDrive\resodrive.exe");
        var result = new ScheduledTaskAutostartService(current, UserId, tasks).MigrateFrom(_applicationPath);
        Assert.True(result.Succeeded);
        Assert.Equal(enabled, tasks.Record!.Enabled);
        Assert.True(ScheduledTaskDefinition.IsOwned(tasks.Record.Xml, current, UserId));
    }

    [Fact]
    public void Migration_RestoresOriginalTaskWhenVerificationFails()
    {
        var original = new StartupTaskRecord(ScheduledTaskDefinition.CreateXml(_applicationPath, UserId), false);
        var tasks = new FakeTaskStore { Record = original, ThrowVerificationRead = true };
        var result = new ScheduledTaskAutostartService(@"C:\Program Files\ResoDrive\resodrive.exe", UserId, tasks).MigrateFrom(_applicationPath);
        Assert.False(result.Succeeded);
        Assert.Equal(original, tasks.Record);
    }

    [Fact]
    public void Migration_LeavesPortableTaskUnchanged()
    {
        var original = new StartupTaskRecord(ScheduledTaskDefinition.CreateXml(@"D:\Portable\resodrive.exe", UserId), true);
        var tasks = new FakeTaskStore { Record = original };
        var result = new ScheduledTaskAutostartService(@"C:\Program Files\ResoDrive\resodrive.exe", UserId, tasks).MigrateFrom(_applicationPath);
        Assert.False(result.Succeeded);
        Assert.Equal(original, tasks.Record);
    }

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
            Record = new(xml, true);
        }

        public void SetEnabled(string taskName, bool enabled)
        {
            if (Record is not null)
                Record = Record with { Enabled = enabled };
        }

        public void Delete(string taskName) => Record = null;
    }

}
