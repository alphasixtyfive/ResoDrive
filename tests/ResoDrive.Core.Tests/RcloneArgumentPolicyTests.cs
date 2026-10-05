using ResoDrive.Core.Validation;

namespace ResoDrive.Core.Tests;

public sealed class RcloneArgumentPolicyTests
{
    [Fact]
    public void ValidateSync_ReportsEveryIssueWithoutEchoingOversizedOptionNames()
    {
        var result = RcloneArgumentPolicy.ValidateSync(["--" + new string('x', 2046), "--not-approved"]);
        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, issue => Assert.True(issue.Message.Length < 120));
        Assert.Contains("… is not an approved option.", result.Issues[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateMount_AcceptsApprovedTokenizedArguments()
    {
        var result = RcloneArgumentPolicy.ValidateMount(
            ["--vfs-cache-mode", "full", "--dir-cache-time=5m", "--read-only", "--network-mode"]);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("--config", "arguments.managerOwned")]
    [InlineData("--config=other.conf", "arguments.managerOwned")]
    [InlineData("--password-command=cmd.exe", "arguments.externalCommand")]
    [InlineData("--rc", "arguments.remoteControl")]
    [InlineData("--rc-no-auth", "arguments.remoteControl")]
    [InlineData("--dump-headers", "arguments.dump")]
    [InlineData("--metadata-mapper=tool.exe", "arguments.externalCommand")]
    [InlineData("--unknown-option", "arguments.unsupported")]
    [InlineData("--", "arguments.terminator")]
    [InlineData("remote:path", "arguments.positional")]
    public void ValidateMount_RejectsUnsafeOrUnapprovedToken(string token, string code)
    {
        var result = RcloneArgumentPolicy.ValidateMount([token]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == code);
    }

    [Fact]
    public void ValidateMount_RejectsDuplicateOptionAcrossValueForms()
    {
        var result = RcloneArgumentPolicy.ValidateMount(
            ["--timeout", "1m", "--timeout=2m"]);

        Assert.Contains(result.Issues, issue => issue.Code == "arguments.duplicate");
    }

    [Fact]
    public void ValidateMount_RejectsMissingValue()
    {
        var result = RcloneArgumentPolicy.ValidateMount(["--timeout", "--read-only"]);

        Assert.Contains(result.Issues, issue => issue.Code == "arguments.missingValue");
    }

    [Fact]
    public void ValidateMount_RejectsValueOnSwitch()
    {
        var result = RcloneArgumentPolicy.ValidateMount(["--read-only=true"]);

        Assert.Contains(result.Issues, issue => issue.Code == "arguments.unexpectedValue");
    }

    [Fact]
    public void ValidateSync_AcceptsSyncOptionsAndRejectsMountOnlyOptions()
    {
        Assert.True(RcloneArgumentPolicy.ValidateSync(["--checksum", "--max-age=7d"]).IsValid);
        Assert.Contains(
            RcloneArgumentPolicy.ValidateSync(["--vfs-cache-mode=full"]).Issues,
            issue => issue.Code == "arguments.unsupported");
    }

    [Fact]
    public void ValidateMount_RejectsControlCharactersAndOversizedInput()
    {
        var control = RcloneArgumentPolicy.ValidateMount(["--timeout", "1m\n--rc"]);
        var inlineControl = RcloneArgumentPolicy.ValidateMount(["--timeout=1m\n--rc"]);
        var oversized = RcloneArgumentPolicy.ValidateMount(
            Enumerable.Repeat("--read-only", 65).ToArray());

        Assert.Contains(control.Issues, issue => issue.Code == "arguments.controlCharacter");
        Assert.Contains(inlineControl.Issues, issue => issue.Code == "arguments.controlCharacter");
        Assert.Contains(oversized.Issues, issue => issue.Code == "arguments.tooMany");
    }

    [Theory]
    [InlineData("--max-size=banana")]
    [InlineData("--min-size=2GB")]
    [InlineData("--max-size=8E")]
    [InlineData("--max-age=banana")]
    [InlineData("--min-age=999999999999h")]
    [InlineData("--timeout=9223372036854775808ns")]
    [InlineData("--bwlimit=banana")]
    [InlineData("--bwlimit=2MB")]
    [InlineData("--bwlimit=10M:")]
    [InlineData("--bwlimit=24:00,10M")]
    [InlineData("--bwlimit=08:60,10M")]
    [InlineData("--bwlimit=Funday-08:00,10M")]
    [InlineData("--bwlimit=Mon-08:00,10M Tue-18:00,banana")]
    public void ValidateSync_RejectsInvalidTypedAdvancedValues(string option) =>
        Assert.Contains(RcloneArgumentPolicy.ValidateSync([option]).Issues,
            issue => issue.Code == "arguments.invalidValue");

    [Theory]
    [InlineData("--max-size=3.25GiB")]
    [InlineData("--min-size=2Mi")]
    [InlineData("--max-age=7d")]
    [InlineData("--min-age=2026-01-02")]
    [InlineData("--max-age=2026-01-02T03:04:05Z")]
    [InlineData("--max-age=2026-01-02T03:04:05.123+01:00")]
    [InlineData("--timeout=9223372036854775807ns")]
    [InlineData("--bwlimit=10M")]
    [InlineData("--bwlimit=10M:off")]
    [InlineData("--bwlimit=08:00,10M 18:00,off")]
    [InlineData("--bwlimit=Mon-08:00,10M:5M;Tuesday-18:00,off")]
    public void ValidateSync_AcceptsCustomSizeAgeAndBandwidthValues(string option) =>
        Assert.True(RcloneArgumentPolicy.ValidateSync([option]).IsValid);
}
