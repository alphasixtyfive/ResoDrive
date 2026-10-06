namespace ResoDrive.Windows.Tests;

public sealed class ApplicationLauncherTests
{
    [Fact]
    public void Resolve_OnlyUsesAdjacentLauncherForTheApplication()
    {
        var directory = Path.Combine(Path.GetTempPath(), "resodrive-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var application = Path.Combine(directory, "resodrive.exe");
            var helper = Path.Combine(directory, "resodrive-update-helper.exe");
            var launcher = Path.Combine(directory, ApplicationLauncher.FileName);
            Assert.Equal(application, ApplicationLauncher.Resolve(application));
            File.WriteAllText(launcher, "fixture");
            Assert.Equal(launcher, ApplicationLauncher.Resolve(application));
            Assert.Equal(helper, ApplicationLauncher.Resolve(helper));
            var start = ApplicationLauncher.CreateStartInfo(application);
            Assert.Equal(launcher, start.FileName);
            Assert.Equal(directory, start.WorkingDirectory);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.False(start.Environment.ContainsKey(ApplicationLauncher.SupervisedEnvironmentVariable));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
