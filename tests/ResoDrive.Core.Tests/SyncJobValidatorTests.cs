using ResoDrive.Core.Domain;
using ResoDrive.Core.Validation;

namespace ResoDrive.Core.Tests;

public sealed class SyncJobValidatorTests
{
    private readonly SyncJobValidator _validator = new();

    [Fact]
    public void Validate_AcceptsValidJob()
    {
        Assert.True(_validator.Validate(ValidationTestData.ValidSync()).IsValid);
    }

    [Fact]
    public void Validate_RejectsEmptyIdAndName()
    {
        var job = ValidationTestData.ValidSync() with
        {
            Id = new SyncJobId(Guid.Empty),
            DisplayName = " "
        };

        var result = _validator.Validate(job);

        Assert.Contains(result.Issues, issue => issue.Code == "sync.id");
        Assert.Contains(result.Issues, issue => issue.Code == "sync.displayName.required");
    }

    [Theory]
    [InlineData(@"relative\path", "path.local.absolute")]
    [InlineData(@"C:\", "path.local.root")]
    [InlineData(@"C:\Data\..\Windows", "path.local.traversal")]
    public void Validate_RejectsUnsafeLocalPath(string path, string code)
    {
        var result = _validator.Validate(ValidationTestData.ValidSync() with { LocalPath = path });

        Assert.Contains(result.Issues, issue => issue.Code == code);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("folder/./child")]
    [InlineData("folder\\child")]
    [InlineData("//rooted/path")]
    [InlineData("folder//child")]
    public void Validate_RejectsInvalidRemotePath(string path)
    {
        Assert.False(_validator.Validate(ValidationTestData.ValidSync() with { RemotePath = path }).IsValid);
    }

    [Fact]
    public void Validate_AcceptsAbsoluteRemotePath()
    {
        var result = _validator.Validate(
            ValidationTestData.ValidSync() with { RemotePath = "/srv/harbour" });

        Assert.True(result.IsValid);
    }


    [Theory]
    [InlineData(@"C:\Data\*.txt")]
    [InlineData(@"C:\Data\file:stream")]
    [InlineData(@"\\?\C:\Data")]
    public void Validate_RejectsReservedWindowsPathSyntax(string path)
    {
        Assert.Contains(
            _validator.Validate(ValidationTestData.ValidSync() with { LocalPath = path }).Issues,
            issue => issue.Code == "path.local.invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_441)]
    public void Validate_RejectsScheduleOutsideFiveMinutesToOneDay(int minutes)
    {
        var job = ValidationTestData.ValidSync() with
        {
            Schedule = new SyncSchedule { Enabled = true, Interval = TimeSpan.FromMinutes(minutes) }
        };

        Assert.Contains(_validator.Validate(job).Issues, issue => issue.Code == "sync.schedule.interval");
    }

    [Fact]
    public void Validate_AllowsManualScheduleRegardlessOfStoredInterval()
    {
        var job = ValidationTestData.ValidSync() with
        {
            Schedule = new SyncSchedule { Enabled = false, Interval = TimeSpan.Zero }
        };

        Assert.True(_validator.Validate(job).IsValid);
    }

    [Fact]
    public void Validate_RejectsInvalidModeAndUnsafeArguments()
    {
        var job = ValidationTestData.ValidSync(arguments: ["--rc"]) with
        {
            Mode = (SyncMode)999
        };

        var result = _validator.Validate(job);

        Assert.Contains(result.Issues, issue => issue.Code == "sync.mode");
        Assert.Contains(result.Issues, issue => issue.Code == "arguments.remoteControl");
    }

    [Fact]
    public void Validate_RejectsBisyncUntilRecoveryIsSupported()
    {
        var result = _validator.Validate(ValidationTestData.ValidSync() with { Mode = SyncMode.Bisync });

        Assert.Contains(result.Issues, issue => issue.Code == "sync.mode.bisync");
    }

    [Theory]
    [InlineData(SyncMode.CopyFromRemote)]
    [InlineData(SyncMode.SyncFromRemote)]
    public void Validate_AcceptsManagedDownloadModes(SyncMode mode)
    {
        var result = _validator.Validate(ValidationTestData.ValidSync() with
        {
            ManagedLocalCopy = true,
            Mode = mode
        });

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(SyncMode.CopyToRemote)]
    [InlineData(SyncMode.SyncToRemote)]
    [InlineData(SyncMode.Bisync)]
    public void Validate_RejectsManagedModesThatCanWriteToRemote(SyncMode mode)
    {
        var result = _validator.Validate(ValidationTestData.ValidSync() with
        {
            ManagedLocalCopy = true,
            Mode = mode
        });

        Assert.Contains(result.Issues, issue => issue.Code == "sync.managed_direction");
    }
}
