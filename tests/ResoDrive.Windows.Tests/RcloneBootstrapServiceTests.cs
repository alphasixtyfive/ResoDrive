using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace ResoDrive.Windows.Tests;

[Collection(RcloneRuntimeMutationTestGroup.Name)]
public sealed class RcloneBootstrapServiceTests
{
    [Fact]
    public async Task InstallAsync_DownloadsVerifiesAndInstallsPinnedRuntime()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/rclone.exe", "new"));
        using var harness = new Harness(archive);
        var updates = new List<RcloneBootstrapProgress>();

        var result = await harness.Service.InstallAsync(new InlineProgress(updates.Add));

        Assert.True(result.Succeeded);
        Assert.Equal(RcloneBootstrapService.ReleaseVersion, result.Value!.Version);
        Assert.Equal("new", File.ReadAllText(harness.Paths.RcloneExecutable));
        Assert.Equal(1, harness.Handler.RequestCount);
        Assert.Contains(updates, update =>
            update.Message == "Downloading rclone" && update.Percentage == 100d);
        Assert.Contains(updates, update => update.Message == "Verifying download");
        Assert.Contains(updates, update => update.Message == "Installing rclone");
        AssertNoTransactionFiles(harness.Paths.Rclone);
    }

    [Fact]
    public async Task InstallAsync_RejectsBadHashWithoutInstallingRuntime()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/rclone.exe", "new"));
        using var harness = new Harness(archive, expectedHash: new string('0', 64));

        var result = await harness.Service.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.download_hash", result.Error!.Code);
        Assert.False(File.Exists(harness.Paths.RcloneExecutable));
        Assert.Empty(harness.Runner.Calls);
        AssertNoTransactionFiles(harness.Paths.Rclone);
    }

    [Fact]
    public async Task InstallAsync_ReplacesInvalidExistingRuntimeAndRemovesBackup()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/rclone.exe", "new"));
        using var harness = new Harness(archive);
        harness.Paths.EnsureCreated();
        File.WriteAllText(harness.Paths.RcloneExecutable, "invalid-old");

        var result = await harness.Service.InstallAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(harness.Paths.RcloneExecutable));
        Assert.Equal(1, harness.Handler.RequestCount);
        AssertNoTransactionFiles(harness.Paths.Rclone);
    }

    [Fact]
    public async Task InstallAsync_RejectsMalformedArchiveAndCleansTransactionFiles()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/README.txt", "not an executable"));
        using var harness = new Harness(archive);

        var result = await harness.Service.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.install_failed", result.Error!.Code);
        Assert.False(File.Exists(harness.Paths.RcloneExecutable));
        Assert.Empty(harness.Runner.Calls);
        AssertNoTransactionFiles(harness.Paths.Rclone);
    }

    [Fact]
    public async Task InstallAsync_RecoversVerifiedPreviousRuntimeWithoutDownloadingAgain()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/rclone.exe", "new"));
        using var harness = new Harness(archive);
        harness.Paths.EnsureCreated();
        File.WriteAllText(
            Path.Combine(harness.Paths.Rclone, $".previous-{Guid.NewGuid():N}.exe"),
            "new");

        var result = await harness.Service.InstallAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(harness.Paths.RcloneExecutable));
        Assert.Equal(0, harness.Handler.RequestCount);
        AssertNoTransactionFiles(harness.Paths.Rclone);
    }

    [Fact]
    public async Task InstallAsync_DoesNotDeleteUpdaterRollbackBackup()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/rclone.exe", "new"));
        using var harness = new Harness(archive, expectedHash: new string('0', 64));
        harness.Paths.EnsureCreated();
        File.WriteAllText(harness.Paths.RcloneExecutable, "invalid");
        var updaterBackup = Path.Combine(
            harness.Paths.Rclone,
            $".rclone-old-{Guid.NewGuid():N}.exe");
        File.WriteAllText(updaterBackup, "known-good-update-backup");

        var result = await harness.Service.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.download_hash", result.Error!.Code);
        Assert.True(File.Exists(updaterBackup));
        Assert.Equal("known-good-update-backup", File.ReadAllText(updaterBackup));
    }

    [Fact]
    public async Task InstallAsync_RestoresPreviousRuntimeWhenCommittedCopyFailsVerification()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/rclone.exe", "new"));
        using var harness = new Harness(archive);
        harness.Paths.EnsureCreated();
        File.WriteAllText(harness.Paths.RcloneExecutable, "old");
        harness.Runner.RejectCanonicalRuntime = true;

        var result = await harness.Service.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.install_verify", result.Error!.Code);
        Assert.Equal("old", File.ReadAllText(harness.Paths.RcloneExecutable));
        AssertNoTransactionFiles(harness.Paths.Rclone);
    }

    [Fact]
    public async Task InstallAsync_CancellationRemovesPartialDownloadAndReleasesMutationLock()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rdrive-bootstrap-tests",
            Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var runner = new FakeRunner(paths.RcloneExecutable);
        var handler = new BlockingArchiveHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new RcloneBootstrapService(
            new RcloneRuntimeLocator(paths, runner),
            runner,
            client,
            new string('0', 64));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var install = service.InstallAsync(cancellationToken: cancellation.Token);
            await handler.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(File.Exists(Path.Combine(paths.Rclone, ".rclone-download.zip")));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);

            AssertNoTransactionFiles(paths.Rclone);
            using var lockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await using (var mutationLock = await RcloneRuntimeMutationLock.AcquireAsync(
                paths.Rclone,
                lockTimeout.Token))
            {
            }
            Assert.False(File.Exists(Path.Combine(paths.Rclone, "runtime.lock")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BodyDeadlineTimesOutAndReleasesRuntimeMutationLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "rdrive-bootstrap-timeout-" + Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var runner = new FakeRunner(paths.RcloneExecutable);
        using var handler = new BlockingArchiveHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new RcloneBootstrapService(new RcloneRuntimeLocator(paths, runner), runner, client,
            new string('0', 64), TimeSpan.FromMilliseconds(500));
        try
        {
            var install = service.InstallAsync();
            await handler.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await install.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("rclone.download_timeout", result.Error?.Code);
            Assert.False(File.Exists(paths.RcloneExecutable));
            Assert.True(File.Exists(Path.Combine(paths.Rclone, ".rclone-download.zip")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await using var mutationLock = await RcloneRuntimeMutationLock.AcquireAsync(paths.Rclone, timeout.Token);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static byte[] CreateArchive(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(item.Content);
            }
        }

        return output.ToArray();
    }

    private static string Hash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static void AssertNoTransactionFiles(string directory)
    {
        Assert.DoesNotContain(
            Directory.EnumerateFiles(directory),
            path => Path.GetFileName(path).StartsWith('.'));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly HttpClient _client;

        public Harness(byte[] archive, string? expectedHash = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "rdrive-bootstrap-tests",
                Guid.NewGuid().ToString("N"));
            Paths = new ApplicationPaths(_root);
            Runner = new FakeRunner(Paths.RcloneExecutable);
            Handler = new ArchiveHandler(archive);
            _client = new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(5) };
            var locator = new RcloneRuntimeLocator(Paths, Runner);
            Service = new RcloneBootstrapService(
                locator,
                Runner,
                _client,
                expectedHash ?? Hash(archive));
        }

        public ApplicationPaths Paths { get; }
        public FakeRunner Runner { get; }
        public ArchiveHandler Handler { get; }
        public RcloneBootstrapService Service { get; }

        public void Dispose()
        {
            _client.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ArchiveHandler(byte[] archive) : HttpMessageHandler
    {
        private readonly byte[] _archive = archive;
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_archive),
                RequestMessage = request,
            });
        }
    }

    private sealed class BlockingArchiveHandler : HttpMessageHandler
    {
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream(ReadStarted)),
                RequestMessage = request,
            });
    }

    private sealed class BlockingReadStream(TaskCompletionSource readStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class InlineProgress(Action<RcloneBootstrapProgress> report)
        : IProgress<RcloneBootstrapProgress>
    {
        public void Report(RcloneBootstrapProgress value) => report(value);
    }

    [Fact]
    public async Task InstallAsync_RestoresPreviousRuntimeWhenWindowsBlocksCommittedExecutable()
    {
        var archive = CreateArchive(("rclone-v1.75.0-windows-amd64/rclone.exe", "new"));
        using var harness = new Harness(archive);
        harness.Paths.EnsureCreated();
        File.WriteAllText(harness.Paths.RcloneExecutable, "old");
        harness.Runner.BlockCanonicalRuntime = true;
        var result = await harness.Service.InstallAsync();
        Assert.False(result.Succeeded);
        Assert.Equal("old", File.ReadAllText(harness.Paths.RcloneExecutable));
        AssertNoTransactionFiles(harness.Paths.Rclone);
    }

    private sealed class FakeRunner(string canonicalPath) : IRcloneProcessRunner
    {
        public List<string> Calls { get; } = [];
        public bool RejectCanonicalRuntime { get; set; }
        public bool BlockCanonicalRuntime { get; set; }

        public Task<ProcessRunResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string>? standardErrorLineReceived = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(executablePath);
            if (BlockCanonicalRuntime && executablePath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new System.ComponentModel.Win32Exception(5);
            var valid = File.Exists(executablePath) &&
                File.ReadAllText(executablePath).Equals("new", StringComparison.Ordinal) &&
                !(RejectCanonicalRuntime && Path.GetFullPath(executablePath).Equals(
                    Path.GetFullPath(canonicalPath),
                    StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(valid
                ? new ProcessRunResult(
                    0,
                    $"rclone {RcloneBootstrapService.ReleaseVersion}{Environment.NewLine}",
                    string.Empty,
                    false)
                : new ProcessRunResult(1, string.Empty, "invalid", false));
        }
    }
}
