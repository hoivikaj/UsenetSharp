using System.Buffers;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private static readonly byte[] DateCommand = "DATE\r\n"u8.ToArray();

    private void CleanupConnection(bool createNewLifetime = true)
    {
        Volatile.Write(ref _connectionState, 0);
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
        Exception? backgroundException;
        lock (_stateLock)
        {
            backgroundException = _backgroundException?.SourceException;
        }

        if (backgroundException != null)
        {
            throw new UsenetProtocolException(
                "connection unusable after an earlier interrupted operation",
                backgroundException);
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

    private static void ValidateSegmentId(SegmentId segmentId)
    {
        var value = segmentId.Value;
        ValidateCommandValue(value, nameof(segmentId), 497);
        if (value.Length < 3 || value[0] == '@' || value[^1] == '@' || !value.Contains('@') ||
            value.Contains('<') || value.Contains('>') || ContainsWhitespace(value))
        {
            throw new ArgumentException("Segment ID must be a valid NNTP message-id without angle brackets.",
                nameof(segmentId));
        }
    }

    private static void ValidateCommandValue(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ValidateCommandValue(value.AsSpan(), parameterName, maximumLength);
    }

    private static void ValidateCommandValue(
        ReadOnlySpan<char> value,
        string parameterName,
        int maximumLength)
    {
        if (value.Length == 0 || value.Length > maximumLength ||
            ContainsControlCharacter(value))
        {
            throw new ArgumentException(
                $"Value must contain 1-{maximumLength} characters and no control characters.",
                parameterName);
        }
    }

    private static bool ContainsWhitespace(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsControlCharacter(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private CancellationTokenSource CreateCtsWithTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ReadTimeout);
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

    private ValueTask WriteMessageIdCommandAsync(
        string command,
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        var messageId = segmentId.Value;
        var length = command.Length + messageId.Length + 5;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var destination = buffer.AsSpan(0, length);
            var written = Encoding.Latin1.GetBytes(command, destination);
            destination[written++] = (byte)' ';
            destination[written++] = (byte)'<';
            written += Encoding.Latin1.GetBytes(messageId, destination[written..]);
            destination[written++] = (byte)'>';
            destination[written++] = (byte)'\r';
            destination[written++] = (byte)'\n';
            return WritePooledCommandAsync(buffer, written, cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private async ValueTask WritePooledCommandAsync(
        byte[] buffer,
        int length,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CreateCtsWithTimeout(cancellationToken);
            try
            {
                await _stream!.WriteAsync(buffer.AsMemory(0, length), cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timeout writing to NNTP stream.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask WriteCommandAsync(
        ReadOnlyMemory<byte> command,
        CancellationToken cancellationToken)
    {
        using var cts = CreateCtsWithTimeout(cancellationToken);
        try
        {
            await _stream!.WriteAsync(command, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
        CoalescedReadTimeout readTimeout)
    {
        readTimeout.BeginRead();
        try
        {
            return await _reader!.ReadLineBytesAsync(readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (readTimeout.IsTimeoutCancellation)
        {
            throw new TimeoutException("Timeout reading from NNTP stream.");
        }
        finally
        {
            readTimeout.EndRead();
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
