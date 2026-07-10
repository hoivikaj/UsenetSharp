using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private void CleanupConnection(bool createNewLifetime = true)
    {
        _connectionCts.Cancel();
        _reader?.Dispose();
        _writer?.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _connectionCts.Dispose();

        _reader = null;
        _writer = null;
        _stream = null;
        _tcpClient = null;
        if (createNewLifetime)
        {
            _connectionCts = new CancellationTokenSource();
        }

        lock (this)
        {
            _backgroundException = null;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfUnhealthy()
    {
        lock (this)
        {
            _backgroundException?.Throw();
        }
    }

    private CancellationTokenSource CreateOperationTokenSource(CancellationToken cancellationToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _connectionCts.Token);
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
