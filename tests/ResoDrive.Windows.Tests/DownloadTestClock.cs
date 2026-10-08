namespace ResoDrive.Windows.Tests;

// Network stalls and steady slow transfers are simulated without sleeping.
internal sealed class DownloadTestClock : TimeProvider
{
    private long _ticks;
    public ManualTimer Timer { get; private set; } = null!;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Timer = new ManualTimer(this, callback, state);
        Timer.Change(dueTime, period);
        return Timer;
    }
    public void Advance(TimeSpan duration) { _ticks += duration.Ticks; Timer.Tick(_ticks); }

    internal sealed class ManualTimer(DownloadTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _due = long.MaxValue;
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
            return !Disposed;
        }
        public void Tick(long ticks) { if (!Disposed && ticks >= _due) { _due = long.MaxValue; callback(state); } }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
