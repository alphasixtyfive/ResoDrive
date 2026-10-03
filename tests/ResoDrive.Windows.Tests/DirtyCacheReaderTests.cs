using System.Text.Json;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class DirtyCacheReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rdrive-cache-reader", Guid.NewGuid().ToString("N"));
    private string Metadata => Path.Combine(_root, "cache", "vfsMeta", "remote");
    private string Cache => Path.Combine(_root, "cache");
    public DirtyCacheReaderTests() => Directory.CreateDirectory(Metadata);

    [Fact]
    public void ReadsDirtyOpenFilesEvenWhenTheirRecordedSizeIsZero()
    {
        File.WriteAllText(Path.Combine(Metadata, "open.txt"), "{\"Dirty\":true,\"Size\":0}");
        File.WriteAllText(Path.Combine(Metadata, "clean.txt"), "{\"Dirty\":false,\"Size\":100}");
        var status = DirtyCacheReader.Read(Metadata, Cache, CancellationToken.None);
        var file = Assert.Single(status.Files);
        Assert.Equal("open.txt", file.RelativePath);
        Assert.Equal(0, file.TotalBytes);
    }

    [Fact]
    public void IncompleteMetadataCannotBeMistakenForACleanFile()
    {
        File.WriteAllText(Path.Combine(Metadata, "file.txt"), "{\"Size\":100}");
        Assert.ThrowsAny<JsonException>(() => DirtyCacheReader.Read(Metadata, Cache, CancellationToken.None));
    }

    [Fact]
    public void PartiallyWrittenMetadataCannotClearPendingWork()
    {
        File.WriteAllText(Path.Combine(Metadata, "file.txt"), "{\"Dirty\":");
        Assert.ThrowsAny<JsonException>(() => DirtyCacheReader.Read(Metadata, Cache, CancellationToken.None));
    }

    [Fact]
    public void RejectsCacheEscapeAndSiblingPrefix()
    {
        Assert.Throws<IOException>(() => DirtyCacheReader.ValidatePath(_root, Cache));
        Assert.Throws<IOException>(() => DirtyCacheReader.ValidatePath(Path.Combine(Cache, "vfsMeta-other", "remote"), Cache));
        Assert.Throws<IOException>(() => DirtyCacheReader.ValidatePath(Path.Combine(Metadata, "..", "..", "outside"), Cache));
    }

    [Fact]
    public void AcceptsNormalizedWindowsExtendedPrefix()
    {
        Assert.Equal(Metadata, DirtyCacheReader.ValidatePath("\\\\?\\" + Metadata, Cache));
    }

    [Fact]
    public void CancelledScanIsNeverACompletedCleanScan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DirtyCacheReader.Read(Metadata, Cache, cancellation.Token));
    }

    [Fact]
    public void ReparseAncestorsCannotRedirectInspectionOutsideManagedCache()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(Cache, "vfsMeta", "linked");
        // Junctions do not need developer mode or administrator permissions.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"", RedirectStandardOutput = true, RedirectStandardError = true
        });
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        Assert.Throws<IOException>(() => DirtyCacheReader.ValidatePath(Path.Combine(link, "subfolder"), Cache));
    }

    public void Dispose()
    {
        var link = Path.Combine(Cache, "vfsMeta", "linked");
        if (Directory.Exists(link)) Directory.Delete(link);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
