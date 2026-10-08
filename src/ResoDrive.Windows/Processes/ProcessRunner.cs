using System.Diagnostics;
using System.Text;
namespace ResoDrive.Windows;

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

public static class ProcessRunner
{
    private const int MaximumCapturedCharacters = 256 * 1024;
    internal const int MaximumLogLineCharacters = 64 * 1024;
    private static readonly TimeSpan ForcedStopTimeout = TimeSpan.FromSeconds(3);

    public static async Task<ProcessRunResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            executablePath,
            arguments,
            timeout,
            standardInput: null,
            environment: null,
            standardErrorLineReceived: null,
            cancellationToken).ConfigureAwait(false);

    public static Task<ProcessRunResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        string? standardInput,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            executablePath,
            arguments,
            timeout,
            standardInput,
            environment,
            standardErrorLineReceived: null,
            cancellationToken);

    public static async Task<ProcessRunResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        string? standardInput,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? standardErrorLineReceived,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var started = await Task.Run(process.Start, cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            throw new InvalidOperationException($"Could not start '{Path.GetFileName(executablePath)}'.");
        }

        var output = new OutputCapture();
        var error = new OutputCapture();
        var outputTask = ReadBoundedAsync(process.StandardOutput, output, linkedSource.Token);
        var errorTask = ReadBoundedAsync(
            process.StandardError,
            standardErrorLineReceived,
            error,
            linkedSource.Token);
        ObserveFailure(outputTask);
        ObserveFailure(errorTask);
        var drain = Task.WhenAll(outputTask, errorTask);
        ObserveFailure(drain);

        var timedOut = false;
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), linkedSource.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            // A child can inherit the pipes and keep them open after its parent
            // exits. Output draining belongs to the same execution deadline.
            await drain.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            linkedSource.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            await StopProcessAsync(process).ConfigureAwait(false);
        }
        catch
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Closing our readers interrupts outstanding pipe reads. Do not wait
            // indefinitely for a descendant or an observer that ignores cancellation.
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }

        return new ProcessRunResult(process.ExitCode, output.ToString(), error.ToString(), timedOut);
    }

    private static void ObserveFailure(Task read) => _ = read.ContinueWith(
        static completed => { _ = completed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        Action<string>? lineReceived,
        OutputCapture capture,
        CancellationToken cancellationToken)
    {
        if (lineReceived is null)
        {
            return await ReadBoundedAsync(reader, capture, cancellationToken).ConfigureAwait(false);
        }

        return await ReadBoundedLinesAsync(reader, lineReceived, capture, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ReadBoundedLinesAsync(StreamReader reader, Action<string> lineReceived,
        CancellationToken cancellationToken) =>
        await ReadBoundedLinesAsync(reader, lineReceived, new OutputCapture(), cancellationToken).ConfigureAwait(false);

    private static async Task<string> ReadBoundedLinesAsync(StreamReader reader, Action<string> lineReceived,
        OutputCapture result, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var pending = new StringBuilder(MaximumLogLineCharacters);
        var truncated = false;
        var previousWasCarriageReturn = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (value is '\r' or '\n')
                {
                    if (value != '\n' || !previousWasCarriageReturn) EmitLine();
                    previousWasCarriageReturn = value == '\r';
                    continue;
                }
                previousWasCarriageReturn = false;
                if (pending.Length < MaximumLogLineCharacters) pending.Append(value);
                else truncated = true;
            }
        }
        if (pending.Length > 0 || truncated) EmitLine();
        return result.ToString();

        void EmitLine()
        {
            var line = pending.ToString();
            if (truncated) line += " [truncated]";
            result.Append(line);
            result.Append(Environment.NewLine);
            pending.Clear();
            truncated = false;
            try { lineReceived(line); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // An observer must never interrupt draining or strand the child process.
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, OutputCapture result,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return result.ToString();
            }

            result.Append(buffer.AsSpan(0, count));
        }
    }

    private sealed class OutputCapture
    {
        private readonly BoundedTextBuffer _text = new(MaximumCapturedCharacters);

        public void Append(ReadOnlySpan<char> value)
        {
            lock (_text) _text.Append(value);
        }

        public override string ToString()
        {
            lock (_text) return _text.ToString();
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        ProcessTermination.TryKillTree(process);

        if (!await ProcessTermination.WaitForExitAsync(
                process,
                ForcedStopTimeout,
                CancellationToken.None)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(process.StartInfo.FileName)}' could not be terminated.");
        }
    }
}
