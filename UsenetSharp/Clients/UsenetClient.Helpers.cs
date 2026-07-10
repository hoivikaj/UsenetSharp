using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private void CleanupConnection(bool createNewLifetime = true)
    {
        lock (_connectionCtsLock)
        {
            _connectionCts.Cancel();
            _connectionCts.Dispose();
            if (createNewLifetime)
            {
                _connectionCts = new CancellationTokenSource();
            }
        }

        DisposeConnectionResource(_reader);
        DisposeConnectionResource(_writer);
        DisposeConnectionResource(_stream);
        DisposeConnectionResource(_tcpClient);

        _reader = null;
        _writer = null;
        _stream = null;
        _tcpClient = null;

        lock (_stateLock)
        {
            _backgroundException = null;
        }
    }

    private static void DisposeConnectionResource(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (IOException)
        {
            // A failed connection may also fail while flushing or closing.
        }
        catch (ObjectDisposedException)
        {
            // Concurrent network cancellation may have already closed the resource.
        }
    }

    private int ParseResponseCode(string? response)
    {
        if (string.IsNullOrEmpty(response) || response.Length < 3)
        {
            throw new UsenetProtocolException($"Invalid NNTP Response: {response}");
        }

        if (int.TryParse(response.AsSpan(0, 3), out var code))
        {
            return code;
        }

        throw new UsenetProtocolException($"Invalid NNTP Response: {response}");
    }

    private void ThrowIfNotConnected()
    {
        if (_writer == null || _reader == null || _tcpClient == null || !_tcpClient.Connected)
        {
            throw new UsenetNotConnectedException("Not connected to server. Call ConnectAsync first.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    private void ThrowIfUnhealthy()
    {
        lock (_stateLock)
        {
            _backgroundException?.Throw();
        }
    }

    private CancellationTokenSource CreateOperationTokenSource(CancellationToken cancellationToken)
    {
        lock (_connectionCtsLock)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _connectionCts.Token);
        }
    }

    private void CancelConnectionLifetime()
    {
        lock (_connectionCtsLock)
        {
            try
            {
                _connectionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A concurrent cleanup has already ended this connection lifetime.
            }
        }
    }

    private static string ValidateSegmentId(SegmentId segmentId)
    {
        var value = segmentId.ToString();
        ValidateCommandValue(value, nameof(segmentId), 497);
        if (value.Length < 3 || value[0] == '@' || value[^1] == '@' || !value.Contains('@') ||
            value.Contains('<') || value.Contains('>') || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Segment ID must be a valid NNTP message-id without angle brackets.",
                nameof(segmentId));
        }

        return value;
    }

    private static void ValidateCommandValue(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                $"Value must contain 1-{maximumLength} characters and no control characters.",
                parameterName);
        }
    }

    private CancellationTokenSource CreateCtsWithTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return cts;
    }

    private async Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken ct)
    {
        using var cts = CreateCtsWithTimeout(ct);
        try
        {
            await _writer!.WriteLineAsync(line, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout writing to NNTP stream.");
        }
    }

    private async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        using var cts = CreateCtsWithTimeout(ct);
        try
        {
            return await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout reading from NNTP stream.");
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>?> ReadLineBytesAsync(
        CancellationTokenSource timeoutCts,
        CancellationToken operationToken)
    {
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await _reader!.ReadLineBytesAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !operationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout reading from NNTP stream.");
        }
        finally
        {
            if (!timeoutCts.IsCancellationRequested)
            {
                timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async ValueTask<T> RunWithTimeoutAsync<T>(Func<CancellationToken, ValueTask<T>> func, CancellationToken ct)
    {
        using var cts = CreateCtsWithTimeout(ct);
        try
        {
            return await func(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout encountered within NNTP stream.");
        }
    }
}
