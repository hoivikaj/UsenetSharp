namespace UsenetSharp.Clients;

public partial class UsenetClient : IUsenetClient, IDisposable, IAsyncDisposable
{
    private volatile bool _disposed;

    public async Task WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connectionCts.Cancel();
        _commandLock.WaitAsync().GetAwaiter().GetResult();
        CleanupConnection(createNewLifetime: false);
        // Dispose the semaphore while still holding it so queued waiters are
        // faulted instead of being granted a lock that is about to be disposed.
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connectionCts.CancelAsync().ConfigureAwait(false);
        await _commandLock.WaitAsync().ConfigureAwait(false);
        CleanupConnection(createNewLifetime: false);
        // Dispose the semaphore while still holding it so queued waiters are
        // faulted instead of being granted a lock that is about to be disposed.
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
