namespace IntercomServer;

internal class AlarmManager
{
    private readonly Lock _syncRoot = new();

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

    public IDisposable SetAlarm(TimeSpan interval, Func<Task> action)
    {
        var state = new TimerState(action);

        state.Timer = new Timer(TimerCallback, state, interval, Timeout.InfiniteTimeSpan);

        return new Finalizer(() =>
        {
            lock (_syncRoot)
            {
                state.Timer.Dispose();
            }
        });
    }

    private void TimerCallback(object? state)
    {
        var timerState = (TimerState)state!;

        timerState.Timer!.Dispose();

        OnAlarmExpired(new AlarmExpiredEventArgs(timerState.Action));
    }

    protected virtual void OnAlarmExpired(AlarmExpiredEventArgs e)
    {
        AlarmExpired?.Invoke(this, e);
    }

    internal class TimerState(Func<Task> action)
    {
        public Func<Task> Action { get; } = action;
        public Timer? Timer { get; set; }
    }
}

internal class AlarmExpiredEventArgs(Func<Task> action) : EventArgs
{
    public Func<Task> Action { get; } = action;
}
