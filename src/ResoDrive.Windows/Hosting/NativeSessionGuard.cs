using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ResoDrive.Windows.Hosting;

public sealed record NativeSessionGuardStatus(
    bool WindowReady,
    bool ShutdownReasonRegistered,
    bool AutomaticSleepPrevented,
    string? Error);

/// <summary>
/// Keeps the host represented in Windows shutdown even when its tray UI is closed.
/// Windows can still override this guard when the user chooses to shut down anyway.
/// </summary>
public sealed partial class NativeSessionGuard : IDisposable, IAsyncDisposable
{
    private const uint QueryEndSession = 0x0011;
    private const uint EndSession = 0x0016;
    private const uint Destroy = 0x0002;
    private const uint ApplyProtectionMessage = 0x8001;
    private const uint StopMessage = 0x8002;
    private const uint BarrierMessage = 0x8003;
    private const uint ContinuousExecution = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private const string UnknownReason = "ResoDrive is checking for files that still need uploading.";
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly Thread _thread;
    private readonly WindowProcedure _procedure;
    private readonly TaskCompletionSource<NativeSessionGuardStatus> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource<NativeSessionGuardStatus>> _barriers = new();
    private ProtectionState _state = new(true, UnknownReason);
    private NativeSessionGuardStatus _status = new(false, false, false, null);
    private nint _window;
    private bool _reasonRegistered;
    private bool _sleepPrevented;
    private bool _disposed;

    public NativeSessionGuard()
    {
        _procedure = ProcessWindowMessage;
        _thread = new Thread(RunWindow)
        {
            IsBackground = true,
            Name = "ResoDrive Windows session guard",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool BlocksSessionEnding => Volatile.Read(ref _state).BlocksSessionEnding;
    public NativeSessionGuardStatus Status => Volatile.Read(ref _status);

    public Task<NativeSessionGuardStatus> WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        _ready.Task.WaitAsync(cancellationToken);

    /// <summary>Publish fresh host state without performing native calls on the caller's thread.</summary>
    public void Update(bool blocksSessionEnding, string? reason = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _state, new ProtectionState(blocksSessionEnding, NormalizeReason(reason)));
            if (_window != 0 && !PostMessage(_window, ApplyProtectionMessage, 0, 0))
            {
                PublishFailure("Windows could not update ResoDrive's upload protection.");
            }
        }
    }

    public void Dispose()
    {
        RequestStop();
        if (Thread.CurrentThread != _thread && !_thread.Join(DisposeTimeout))
        {
            PublishFailure("ResoDrive's Windows session guard did not finish closing.");
        }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        if (Thread.CurrentThread != _thread)
        {
            try
            {
                await _stopped.Task.WaitAsync(DisposeTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                PublishFailure("ResoDrive's Windows session guard did not finish closing.");
            }
        }
        GC.SuppressFinalize(this);
    }

    private void RequestStop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_window != 0 && !PostMessage(_window, StopMessage, 0, 0))
            {
                PublishFailure("Windows could not close ResoDrive's session guard.");
            }
        }
    }

    private void RunWindow()
    {
        var className = "ResoDrive.SessionGuard." + Guid.NewGuid().ToString("N");
        var instance = GetModuleHandle(null);
        var classNamePointer = Marshal.StringToHGlobalUni(className);
        ushort registeredClass = 0;
        try
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = instance,
                ClassName = classNamePointer,
            };
            registeredClass = RegisterClass(ref windowClass);
            if (registeredClass == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // A message-only window does not receive WM_QUERYENDSESSION broadcasts.
            var window = CreateWindow(0, className, "ResoDrive upload protection", 0,
                0, 0, 0, 0, 0, 0, instance, 0);
            if (window == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            lock (_gate)
            {
                _window = window;
                if (_disposed)
                {
                    PostMessage(window, StopMessage, 0, 0);
                }
            }
            ApplyProtection();
            _ready.TrySetResult(Status);

            int received;
            while ((received = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            if (received < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            PublishFailure("Windows session protection is unavailable: " + exception.Message);
        }
        finally
        {
            var window = _window;
            ReleaseProtection(window);
            if (window != 0)
            {
                DestroyWindow(window);
            }
            if (registeredClass != 0)
            {
                UnregisterClass(className, instance);
            }
            Marshal.FreeHGlobal(classNamePointer);
            Volatile.Write(ref _status, Status with
            {
                WindowReady = false,
                ShutdownReasonRegistered = false,
                AutomaticSleepPrevented = false,
            });
            lock (_gate)
            {
                _window = 0;
                CompleteBarriers();
            }
            _ready.TrySetResult(Status);
            _stopped.TrySetResult();
            GC.KeepAlive(_procedure);
        }
    }

    private nint ProcessWindowMessage(nint window, uint message, nuint parameter, nint details)
    {
        try
        {
            switch (message)
            {
                case QueryEndSession:
                    ApplyProtection();
                    return BlocksSessionEnding ? 0 : 1;
                case EndSession:
                    // A canceled shutdown must leave protection active. A forced shutdown
                    // must not discard cache or initiate a separate destructive stop path.
                    if (parameter == 0)
                    {
                        ApplyProtection();
                    }
                    return 0;
                case ApplyProtectionMessage:
                    ApplyProtection();
                    return 0;
                case BarrierMessage:
                    ApplyProtection();
                    lock (_gate)
                    {
                        CompleteBarriers();
                    }
                    return 0;
                case StopMessage:
                    ReleaseProtection(window);
                    DestroyWindow(window);
                    return 0;
                case Destroy:
                    ReleaseProtection(window);
                    lock (_gate)
                    {
                        _window = 0;
                    }
                    PostQuitMessage(0);
                    return 0;
                default:
                    return DefWindowProcedure(window, message, parameter, details);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            PublishFailure("Windows upload protection could not be refreshed: " + exception.Message);
            return message == QueryEndSession ? 0 : DefWindowProcedure(window, message, parameter, details);
        }
    }

    private void ApplyProtection()
    {
        var state = Volatile.Read(ref _state);
        var window = _window;
        if (window == 0)
        {
            return;
        }
        string? error = null;
        if (state.BlocksSessionEnding)
        {
            var registered = CreateShutdownReason(window, state.Reason);
            _reasonRegistered |= registered;
            if (!registered)
            {
                error = "Windows could not display the pending-upload shutdown reason.";
            }
            var sleepPrevented = SetThreadExecutionState(ContinuousExecution | SystemRequired) != 0;
            _sleepPrevented |= sleepPrevented;
            if (!sleepPrevented)
            {
                error = "Windows could not prevent automatic sleep while uploads are pending.";
            }
        }
        else
        {
            error = ReleaseProtection(window);
        }
        Volatile.Write(ref _status, new NativeSessionGuardStatus(true, _reasonRegistered, _sleepPrevented, error));
    }

    private string? ReleaseProtection(nint window)
    {
        string? error = null;
        if (_reasonRegistered && window != 0)
        {
            if (DestroyShutdownReason(window))
            {
                _reasonRegistered = false;
            }
            else
            {
                error = "Windows could not remove the completed-upload shutdown reason.";
            }
        }
        if (_sleepPrevented)
        {
            if (SetThreadExecutionState(ContinuousExecution) != 0)
            {
                _sleepPrevented = false;
            }
            else
            {
                error = "Windows could not release ResoDrive's automatic-sleep protection.";
            }
        }
        return error;
    }

    private void PublishFailure(string error) =>
        Volatile.Write(ref _status, Status with { Error = error });

    private void CompleteBarriers()
    {
        while (_barriers.TryDequeue(out var barrier))
        {
            barrier.TrySetResult(Status);
        }
    }

    internal async Task<NativeSessionGuardStatus> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        Task<NativeSessionGuardStatus> synchronized;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_window == 0)
            {
                return Status;
            }
            var completion = new TaskCompletionSource<NativeSessionGuardStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _barriers.Enqueue(completion);
            if (!PostMessage(_window, BarrierMessage, 0, 0))
            {
                PublishFailure("Windows could not synchronize the session guard.");
                CompleteBarriers();
            }
            synchronized = completion.Task;
        }
        return await synchronized.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal nint WindowHandle => Volatile.Read(ref _window);

    internal static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return UnknownReason;
        }
        var normalized = new StringBuilder(240);
        foreach (var character in reason)
        {
            normalized.Append(char.IsControl(character) ? ' ' : character);
            if (normalized.Length >= 240)
            {
                break;
            }
        }
        if (normalized.Length > 0 && char.IsHighSurrogate(normalized[^1]))
        {
            normalized.Length--;
        }
        var result = normalized.ToString().Trim();
        return result.Length == 0 ? UnknownReason : result;
    }

    private sealed record ProtectionState(bool BlocksSessionEnding, string Reason);

}
