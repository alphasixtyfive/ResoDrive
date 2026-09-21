using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;

namespace ResoDrive.Windows.Tests;

public sealed class MountDefinitionMapperTests
{
    [Fact]
    public void ToDomain_RejectsIncompleteNestedSettingsWithoutThrowing()
    {
        var missingTarget = ValidSettings() with { Target = null! };
        var missingSchedule = ValidSettings() with
        {
            SyncJobs = [ValidSettings().SyncJobs[0] with { Schedule = null! }]
        };

        Assert.Equal("mount.settings_incomplete", MountDefinitionMapper.ToDomain(missingTarget).Error?.Code);
        Assert.Equal("sync.settings_incomplete", MountDefinitionMapper.ToDomain(missingSchedule).Error?.Code);
    }

    [Fact]
    public void ToDomain_RejectsInvalidDomainValues()
    {
        var result = MountDefinitionMapper.ToDomain(ValidSettings() with { DisplayName = "" });

        Assert.False(result.Succeeded);
        Assert.Equal("mount.displayName.required", result.Error?.Code);
    }

    [Fact]
    public void Mapping_PreservesConnectionHost()
    {
        var mapped = MountDefinitionMapper.ToDomain(
            ValidSettings() with
            {
                ConnectionHost = "cloud.example.com",
                ConnectionType = "WebDAV",
            });

        Assert.True(mapped.Succeeded);
        Assert.Equal("cloud.example.com", mapped.Value?.ConnectionHost);
        Assert.Equal("WebDAV", mapped.Value?.ConnectionType);
        Assert.Equal(
            "cloud.example.com",
            MountDefinitionMapper.ToSettings(mapped.Value!).ConnectionHost);
        Assert.Equal(
            "WebDAV",
            MountDefinitionMapper.ToSettings(mapped.Value!).ConnectionType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mapping_PreservesManagedLocalCopyAcrossSettingsRoundTrip(bool managed)
    {
        var settings = ValidSettings();
        settings = settings with
        {
            SyncJobs = [settings.SyncJobs[0] with
            {
                ManagedLocalCopy = managed,
                Mode = "copyFromRemote"
            }]
        };

        var mapped = MountDefinitionMapper.ToDomain(settings);

        Assert.True(mapped.Succeeded, mapped.Error?.Message);
        var domainJob = Assert.Single(mapped.Value!.SyncJobs);
        Assert.Equal(managed, domainJob.ManagedLocalCopy);
        var storedJob = Assert.Single(MountDefinitionMapper.ToSettings(mapped.Value).SyncJobs);
        Assert.Equal(managed, storedJob.ManagedLocalCopy);
        Assert.Equal(domainJob.Id.Value, storedJob.Id);
        Assert.Equal(domainJob.LocalPath, storedJob.LocalPath);
        Assert.Equal(SyncMode.CopyFromRemote, domainJob.Mode);
        Assert.Equal("CopyFromRemote", storedJob.Mode);
    }

    [Fact]
    public void Mapping_ExistingSettingsRemainUnmanaged()
    {
        var mapped = MountDefinitionMapper.ToDomain(ValidSettings());

        Assert.True(mapped.Succeeded, mapped.Error?.Message);
        Assert.False(Assert.Single(mapped.Value!.SyncJobs).ManagedLocalCopy);
        Assert.False(Assert.Single(MountDefinitionMapper.ToSettings(mapped.Value).SyncJobs).ManagedLocalCopy);
    }

    private static MountSettings ValidSettings() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Storage",
        RemoteName = "storage",
        Target = new MountTargetSettings { DriveLetter = 'R' },
        SyncJobs =
        [
            new SyncJobSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Backup",
                LocalPath = @"C:\Data",
                RemotePath = "backup"
            }
        ]
    };
}
