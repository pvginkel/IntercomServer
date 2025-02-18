namespace IntercomServer;

internal class Finalizer(Action action) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            action();

            _disposed = true;
        }
    }
}
