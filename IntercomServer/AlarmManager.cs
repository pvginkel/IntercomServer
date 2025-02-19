namespace IntercomServer;

internal class AlarmManager
{
    public event EventHandler<AlarmExpiredEventArgs>? AlarmExpired;

    public IDisposable SetAlarm(TimeSpan interval, Action action)
    {
        return SetAlarm(
            interval,
            () =>
            {
                action();
                return Task.CompletedTask;
            }
        );
    }

    public IDisposable SetAlarm(TimeSpan interval, Func<Task> action) =>
        AddTimer(new TimerState(action, true), interval, Timeout.InfiniteTimeSpan);

    public IDisposable SetInterval(TimeSpan interval, Action action)
    {
        return SetInterval(
            interval,
            () =>
            {
                action();
                return Task.CompletedTask;
            }
        );
    }

    public IDisposable SetInterval(TimeSpan interval, Func<Task> action) =>
        AddTimer(new TimerState(action, false), interval, interval);

    private IDisposable AddTimer(TimerState state, TimeSpan dueTime, TimeSpan period)
    {
        state.Timer = new Timer(TimerCallback, state, dueTime, period);

        return new Finalizer(() => state.Timer.Dispose());
    }

    private void TimerCallback(object? state)
    {
        var timerState = (TimerState)state!;

        if (timerState.OneShot)
            timerState.Timer!.Dispose();

        OnAlarmExpired(new AlarmExpiredEventArgs(timerState.Action));
    }

    protected virtual void OnAlarmExpired(AlarmExpiredEventArgs e)
    {
        AlarmExpired?.Invoke(this, e);
    }

    internal class TimerState(Func<Task> action, bool oneShot)
    {
        public Func<Task> Action { get; } = action;
        public bool OneShot { get; } = oneShot;
        public Timer? Timer { get; set; }
    }
}

internal class AlarmExpiredEventArgs(Func<Task> action) : EventArgs
{
    public Func<Task> Action { get; } = action;
}
