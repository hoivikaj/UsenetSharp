namespace UsenetSharp.Clients;

public partial class UsenetClient : IUsenetClient, IDisposable, IAsyncDisposable
{
    private int _disposeState;

    public UsenetClient()
        : this(new UsenetClientOptions(), TimeProvider.System)
    {
    }

    public UsenetClient(UsenetClientOptions options)
        : this(options, TimeProvider.System)
    {
    }

    internal UsenetClient(UsenetClientOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.ReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "ReadTimeout must be greater than zero.");
        }

        if (options.AbandonedBodyDrainLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "AbandonedBodyDrainLimit cannot be negative.");
        }

        _options = options;
        _timeProvider = timeProvider;
    }

    public bool IsConnected =>
        Volatile.Read(ref _disposeState) == 0 &&
        Volatile.Read(ref _connectionState) != 0;

    public bool IsHealthy
    {
        get
        {
            if (!IsConnected)
            {
                return false;
            }

            lock (_stateLock)
            {
                return _backgroundException == null;
            }
        }
    }

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
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        CancelConnectionLifetime();
        _commandLock.WaitAsync().GetAwaiter().GetResult();
        CleanupConnection(createNewLifetime: false);
        // Dispose the semaphore while still holding it so queued waiters are
        // faulted instead of being granted a lock that is about to be disposed.
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        CancelConnectionLifetime();
        await _commandLock.WaitAsync().ConfigureAwait(false);
        CleanupConnection(createNewLifetime: false);
        // Dispose the semaphore while still holding it so queued waiters are
        // faulted instead of being granted a lock that is about to be disposed.
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
