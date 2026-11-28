using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using ResoDrive.Windows;

namespace ResoDrive.App;

/// <summary>Coordinates per-installation UI activation with per-request replies.</summary>
internal sealed class SingleInstanceActivation : IDisposable
{
    private const byte ShowRequest = 1;
    private const byte ShowAcknowledged = 1;
    private readonly ConcurrentDictionary<ActivationRequest, byte> _requests = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Mutex _instanceMutex;
    private readonly string _pipeName;
    private Task? _listener;
    private bool _disposed;

    internal SingleInstanceActivation(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _pipeName = $"RDrive.Ui.Show.{scope}";
        _instanceMutex = new Mutex(
            initiallyOwned: false,
            $@"Local\RDrive.Ui.Instance.{scope}");
        try
        {
            IsFirstInstance = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            IsFirstInstance = true;
        }
    }

    internal bool IsFirstInstance { get; }

    internal void Listen(Action<ActivationRequest> requestReceived)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(requestReceived);
        if (!IsFirstInstance)
            throw new InvalidOperationException("Only the primary instance can listen for activation requests.");
        if (_listener is not null)
            throw new InvalidOperationException("The activation listener is already running.");

        _listener = Task.Run(() => ListenAsync(requestReceived, _shutdown.Token));
    }

    internal bool RequestShow(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFirstInstance)
            throw new InvalidOperationException("The primary instance cannot request its own activation.");
        return RequestShowPipe(_pipeName, timeout);
    }

    internal static bool RequestShow(string scope, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return RequestShowPipe($"RDrive.Ui.Show.{scope}", timeout);
    }

    private static bool RequestShowPipe(string pipeName, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutCancellation = timeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(timeout);
        try
        {
            return RequestShowAsync(pipeName, timeoutCancellation.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        foreach (var request in _requests.Keys)
            request.Dispose();
        _requests.Clear();
        _shutdown.Dispose();
        if (IsFirstInstance)
            _instanceMutex.ReleaseMutex();
        _instanceMutex.Dispose();
    }

    private async Task ListenAsync(
        Action<ActivationRequest> requestReceived,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CurrentUserPipe.CreateServer(_pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleConnectionAsync(pipe, requestReceived, cancellationToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                return;
            }
            catch (IOException)
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        Action<ActivationRequest> requestReceived,
        CancellationToken cancellationToken)
    {
        NamedPipeServerStream? ownedPipe = pipe;
        try
        {
            var requestByte = new byte[1];
            var bytesRead = await ownedPipe.ReadAsync(requestByte, cancellationToken).ConfigureAwait(false);
            if (bytesRead != 1 || requestByte[0] != ShowRequest)
                return;

            var request = new ActivationRequest(ownedPipe, RemoveRequest);
            ownedPipe = null;
            _requests.TryAdd(request, 0);
            requestReceived(request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            ownedPipe?.Dispose();
        }
    }

    private static async Task<bool> RequestShowAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pipe = CurrentUserPipe.CreateClient(pipeName);
            try
            {
                await pipe.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            CurrentUserPipe.ValidateServerIdentity(pipe);
            await pipe.WriteAsync(new[] { ShowRequest }, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            var response = new byte[1];
            var bytesRead = await pipe.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            return bytesRead == 1 && response[0] == ShowAcknowledged;
        }
    }

    private void RemoveRequest(ActivationRequest request) => _requests.TryRemove(request, out _);

    internal sealed class ActivationRequest : IDisposable
    {
        private readonly Action<ActivationRequest> _completed;
        private NamedPipeServerStream? _pipe;

        internal ActivationRequest(
            NamedPipeServerStream pipe,
            Action<ActivationRequest> completed)
        {
            _pipe = pipe;
            _completed = completed;
        }

        internal bool Acknowledge()
        {
            var pipe = Interlocked.Exchange(ref _pipe, null);
            if (pipe is null)
                return false;
            try
            {
                pipe.WriteByte(ShowAcknowledged);
                pipe.Flush();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            finally
            {
                pipe.Dispose();
                _completed(this);
            }
        }

        public void Dispose()
        {
            var pipe = Interlocked.Exchange(ref _pipe, null);
            pipe?.Dispose();
            _completed(this);
        }
    }
}
