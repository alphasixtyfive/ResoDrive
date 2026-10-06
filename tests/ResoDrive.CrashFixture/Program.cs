using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ResoDrive.CrashFixture;

internal static class Program
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WerSetFlags(uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WerGetFlags(IntPtr process, out uint flags);

    private static int Main(string[] args)
    {
        // This fixture can neither crash locally nor acquire any production account data.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            args.Length != 4 || args[0] != "--identity" || args[2] != "--wait-trigger")
            return 2;

        // Per-process UI suppression; machine dump policy remains unchanged.
        var configured = WerSetFlags(32); // WER_FAULT_REPORTING_NO_UI
        if (configured != 0) return 5;

        using var current = Process.GetCurrentProcess();
        var queried = WerGetFlags(current.Handle, out var flags);
        File.WriteAllText(args[1] + ".runtime.json", JsonSerializer.Serialize(new
        {
            errorMode = GetErrorMode(), werFlags = queried == 0 ? flags : 0,
            werSetResult = configured, debuggerPresent = Debugger.IsAttached
        }));
        File.WriteAllLines(args[1],
        [
            current.Id.ToString(CultureInfo.InvariantCulture),
            current.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture)
        ]);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(args[3]))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) return 4;
            Thread.Sleep(10);
        }

        Environment.FailFast("Disposable ResoDrive .NET 10 WER acceptance fixture.",
            new InvalidOperationException("Known fixture failure; no account data was loaded."));
        return 0;
    }
}
