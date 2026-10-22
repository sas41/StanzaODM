namespace Stanza.Tests.Live;

// Explicitly advanced timers keep live authorization tests independent of wall-clock sleeps.
internal sealed class LiveTestClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ClockTimer> timers = new();
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ClockTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan duration)
    {
        List<ClockTimer> due;
        lock (gate)
        {
            now += duration;
            due = timers.Where(timer => timer.Next <= now).ToList();
            foreach (var timer in due) timer.Next = timer.Period > TimeSpan.Zero ? now + timer.Period : DateTimeOffset.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ClockTimer(LiveTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Next { get; set; }
        public TimeSpan Period { get; private set; }
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed) return false;
                Next = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
                Period = period;
                return true;
            }
        }
        public void Fire() { if (!disposed) callback(state); }
        public void Dispose() { lock (owner.gate) { disposed = true; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
