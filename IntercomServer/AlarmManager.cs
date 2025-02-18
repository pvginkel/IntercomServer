namespace IntercomServer;

internal class AlarmManager
{
    public IDisposable SetAlarm(TimeSpan interval, Action action)
    {
        throw new NotImplementedException();
    }

    public IDisposable SetAlarm(TimeSpan interval, Func<Task> action)
    {
        throw new NotImplementedException();
    }
}
