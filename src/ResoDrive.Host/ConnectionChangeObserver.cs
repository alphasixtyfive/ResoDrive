using System.Net.NetworkInformation;

namespace ResoDrive.Host;

/// <summary>Coalesces network changes and resume into one retry hint for the existing monitor.</summary>
internal sealed class ConnectionChangeObserver : IDisposable
{
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private DateTimeOffset _lastPoll;
    private long _lastChange;
    private bool _pending;
    private bool _disposed;

    internal ConnectionChangeObserver(TimeProvider? clock = null, bool listen = true)
    {
        _clock = clock ?? TimeProvider.System;
        _lastPoll = _clock.GetUtcNow();
        if (!listen) return;
        try
        {
            NetworkChange.NetworkAddressChanged += AddressChanged;
            NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            // Timed destination retries remain available if Windows cannot supply hints.
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        }
    }

    internal void NotifyChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _lastChange = _clock.GetTimestamp();
        }
    }

    internal bool Consume()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            var now = _clock.GetUtcNow();
            // The monitor resumes after sleep without a second Windows message window.
            if (now - _lastPoll > TimeSpan.FromSeconds(15))
            {
                _pending = true;
                _lastChange = _clock.GetTimestamp();
            }
            _lastPoll = now;
            if (!_pending || _clock.GetElapsedTime(_lastChange) < SettleDelay) return false;
            _pending = false;
            return true;
        }
    }

    private void AddressChanged(object? sender, EventArgs args) => NotifyChanged();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => NotifyChanged();

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _pending = false; }
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
    }
}
