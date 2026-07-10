using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;
using RapidYencSharp;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private const int DecodedBodyChunkSize = 64 * 1024;

    public Task<UsenetBodyResponse> BodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return BodyAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetBodyResponse> BodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        var validatedSegmentId = ValidateSegmentId(segmentId);
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;
        CancellationTokenSource? operationCts = null;

        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            operationCts = CreateOperationTokenSource(cancellationToken);

            // Send BODY command with message-id
            await WriteLineAsync($"BODY <{validatedSegmentId}>".AsMemory(), operationCts.Token).ConfigureAwait(false);
            var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - body follows
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                // Create a pipe for streaming the body data
                var pipe = new Pipe(new PipeOptions(
                    pauseWriterThreshold: 1024 * 1024,
                    resumeWriterThreshold: 512 * 1024));

                // Start background task to read the body and write to pipe
                isReadBodyToPipeAsyncStarted = true;
                _ = ReadBodyToPipeAsync(
                    pipe.Writer, operationCts, cancellationToken, onConnectionReadyAgain);
                operationCts = null;

                // Return immediately with the stream and headers
                return new UsenetBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    Stream = pipe.Reader.AsStream(),
                };
            }

            return new UsenetBodyResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                Stream = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                operationCts?.Dispose();
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    public Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        var validatedSegmentId = ValidateSegmentId(segmentId);
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;
        CancellationTokenSource? operationCts = null;

        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            operationCts = CreateOperationTokenSource(cancellationToken);

            await WriteLineAsync($"BODY <{validatedSegmentId}>".AsMemory(), operationCts.Token)
                .ConfigureAwait(false);
            var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
            var responseCode = ParseResponseCode(response);

            if (responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                var pipe = new Pipe(new PipeOptions(
                    pauseWriterThreshold: 1024 * 1024,
                    resumeWriterThreshold: 512 * 1024));
                var headersCompletion =
                    new TaskCompletionSource<UsenetYencHeader?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                isReadBodyToPipeAsyncStarted = true;
                _ = ReadDecodedBodyToPipeAsync(
                    pipe.Writer,
                    headersCompletion,
                    operationCts,
                    cancellationToken,
                    onConnectionReadyAgain);
                operationCts = null;

                return new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    Stream = new YencStream(
                        pipe.Reader.AsStream(), headersCompletion.Task),
                };
            }

            return new UsenetDecodedBodyResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                Stream = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                operationCts?.Dispose();
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    private async Task<DecodedBodyReadResult> ReadDecodedBodyToPipeAsync(
        PipeWriter writer,
        TaskCompletionSource<UsenetYencHeader?> headersCompletion,
        CancellationTokenSource operationCts,
        CancellationToken callerCancellationToken,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        bool releaseCommandLock = true)
    {
        Exception? failure = null;
        var connectionReusable = true;
        byte[]? encodedBuffer = null;
        try
        {
            if (_reader == null)
            {
                throw new UsenetNotConnectedException(
                    "The NNTP connection closed before the article body was read.");
            }

            encodedBuffer = ArrayPool<byte>.Shared.Rent(DecodedBodyChunkSize + 2);
            var encodedLength = 0;
            var shouldWrite = true;
            var dataEnded = false;
            var headersRead = false;
            long drainedBytes = 0;
            string? ybeginLine = null;
            string? ypartLine = null;
            RapidYencDecoderState? decoderState = RapidYencDecoderState.RYDEC_STATE_CRLF;
            uint decodedCrc32 = 0;
            var cancellationToken = operationCts.Token;
            using var readTimeout = new CoalescedReadTimeout(
                cancellationToken, _options.ReadTimeout, _timeProvider);

            while (true)
            {
                ReadOnlyMemory<byte>? lineMemory;
                try
                {
                    lineMemory = await ReadLineBytesAsync(readTimeout)
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                if (!lineMemory.HasValue)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                var lineBytes = lineMemory.Value;
                if (lineBytes.Length == 1 && lineBytes.Span[0] == (byte)'.')
                {
                    if (!headersRead)
                    {
                        if (ybeginLine == null)
                        {
                            throw new InvalidDataException(
                                "Reached end of NNTP body without finding =ybegin header.");
                        }

                        headersCompletion.TrySetResult(
                            YencStream.ParseYencHeaders(ybeginLine, ypartLine));
                    }

                    if (shouldWrite && encodedLength > 0)
                    {
                        var flush = await DecodeAndFlushAsync(
                            writer,
                            encodedBuffer.AsMemory(0, encodedLength),
                            decoderState,
                            decodedCrc32,
                            _options.ValidateDecodedBodyCrc32,
                            cancellationToken).ConfigureAwait(false);
                        decoderState = flush.DecoderState;
                        decodedCrc32 = flush.Crc32;
                    }

                    if (_options.ValidateDecodedBodyCrc32 && !dataEnded)
                    {
                        throw new InvalidDataException(
                            "Reached end of NNTP body without finding a yEnc trailer.");
                    }

                    break;
                }

                if (!shouldWrite)
                {
                    drainedBytes += lineBytes.Length + 2;
                    if (drainedBytes > _options.AbandonedBodyDrainLimit)
                    {
                        throw new UsenetProtocolException(
                            "The abandoned NNTP body exceeded the configured drain limit.");
                    }

                    continue;
                }

                if (dataEnded)
                {
                    continue;
                }

                if (!headersRead)
                {
                    if (ybeginLine == null)
                    {
                        if (YencStream.StartsWithYBegin(lineBytes.Span))
                        {
                            ybeginLine = Encoding.Latin1.GetString(lineBytes.Span);
                        }

                        continue;
                    }

                    if (YencStream.StartsWithYPart(lineBytes.Span))
                    {
                        ypartLine = Encoding.Latin1.GetString(lineBytes.Span);
                        headersRead = true;
                        headersCompletion.TrySetResult(
                            YencStream.ParseYencHeaders(ybeginLine, ypartLine));
                        continue;
                    }

                    headersRead = true;
                    headersCompletion.TrySetResult(
                        YencStream.ParseYencHeaders(ybeginLine, ypartLine));
                }

                if (YencStream.StartsWithYEnd(lineBytes.Span))
                {
                    dataEnded = true;
                    if (encodedLength > 0)
                    {
                        var flush = await DecodeAndFlushAsync(
                            writer,
                            encodedBuffer.AsMemory(0, encodedLength),
                            decoderState,
                            decodedCrc32,
                            _options.ValidateDecodedBodyCrc32,
                            cancellationToken).ConfigureAwait(false);
                        encodedLength = 0;
                        decoderState = flush.DecoderState;
                        decodedCrc32 = flush.Crc32;
                        shouldWrite = !flush.Result.IsCompleted && !flush.Result.IsCanceled;
                    }

                    if (_options.ValidateDecodedBodyCrc32 && shouldWrite)
                    {
                        ValidateDecodedBodyCrc32(
                            lineBytes.Span, ypartLine != null, decodedCrc32);
                    }

                    continue;
                }

                var requiredLength = lineBytes.Length + 2;
                if (encodedLength > 0 &&
                    encodedLength + requiredLength > encodedBuffer.Length)
                {
                    var flush = await DecodeAndFlushAsync(
                        writer,
                        encodedBuffer.AsMemory(0, encodedLength),
                        decoderState,
                        decodedCrc32,
                        _options.ValidateDecodedBodyCrc32,
                        cancellationToken).ConfigureAwait(false);
                    encodedLength = 0;
                    decoderState = flush.DecoderState;
                    decodedCrc32 = flush.Crc32;
                    shouldWrite = !flush.Result.IsCompleted && !flush.Result.IsCanceled;
                    if (!shouldWrite)
                    {
                        continue;
                    }
                }

                if (requiredLength > encodedBuffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(encodedBuffer);
                    encodedBuffer = ArrayPool<byte>.Shared.Rent(requiredLength);
                }

                lineBytes.Span.CopyTo(encodedBuffer.AsSpan(encodedLength));
                encodedLength += lineBytes.Length;
                encodedBuffer[encodedLength++] = (byte)'\r';
                encodedBuffer[encodedLength++] = (byte)'\n';

                if (encodedLength >= DecodedBodyChunkSize)
                {
                    var flush = await DecodeAndFlushAsync(
                        writer,
                        encodedBuffer.AsMemory(0, encodedLength),
                        decoderState,
                        decodedCrc32,
                        _options.ValidateDecodedBodyCrc32,
                        cancellationToken).ConfigureAwait(false);
                    encodedLength = 0;
                    decoderState = flush.DecoderState;
                    decodedCrc32 = flush.Crc32;
                    shouldWrite = !flush.Result.IsCompleted && !flush.Result.IsCanceled;
                }
            }
        }
        catch (OperationCanceledException e) when (callerCancellationToken.IsCancellationRequested)
        {
            failure = e;
            var drainFailure = await TryDrainBodyAsync().ConfigureAwait(false);
            if (drainFailure != null)
            {
                connectionReusable = false;
                RecordBackgroundFailure(drainFailure);
            }
        }
        catch (Exception e)
        {
            failure = e;
            connectionReusable = false;
            RecordBackgroundFailure(e);
        }
        finally
        {
            if (encodedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(encodedBuffer);
            }

            if (failure != null)
            {
                headersCompletion.TrySetException(failure);
            }

            await writer.CompleteAsync(failure).ConfigureAwait(false);
            operationCts.Dispose();
            if (releaseCommandLock)
            {
                _commandLock.Release();
            }

            try
            {
                var result = failure switch
                {
                    null => ArticleBodyResult.Retrieved,
                    OperationCanceledException when connectionReusable =>
                        ArticleBodyResult.Cancelled,
                    _ => ArticleBodyResult.NotRetrieved
                };
                onConnectionReadyAgain?.Invoke(result);
            }
            catch
            {
                // User callbacks must not fault the unobserved background transfer task.
            }
        }

        return new DecodedBodyReadResult(failure, connectionReusable);
    }

    private readonly record struct DecodedBodyReadResult(
        Exception? Failure,
        bool ConnectionReusable);

    private static async ValueTask<(
        FlushResult Result,
        RapidYencDecoderState? DecoderState,
        uint Crc32)> DecodeAndFlushAsync(
        PipeWriter writer,
        ReadOnlyMemory<byte> encoded,
        RapidYencDecoderState? decoderState,
        uint crc32,
        bool computeCrc32,
        CancellationToken cancellationToken)
    {
        var destination = writer.GetSpan(encoded.Length);
        var decodedLength = YencDecoder.DecodeEx(
            encoded.Span, destination, ref decoderState, isRaw: true);
        if (computeCrc32)
        {
            crc32 = Crc32.Compute(destination[..decodedLength], crc32);
        }

        writer.Advance(decodedLength);
        var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (result, decoderState, crc32);
    }

    private static void ValidateDecodedBodyCrc32(
        ReadOnlySpan<byte> yendLine,
        bool isMultipart,
        uint actualCrc32)
    {
        var fieldName = isMultipart ? "pcrc32"u8 : "crc32"u8;
        if (!TryParseYencTrailerCrc32(yendLine, fieldName, out var expectedCrc32))
        {
            throw new InvalidDataException(
                $"The yEnc trailer does not contain a valid {Encoding.ASCII.GetString(fieldName)} value.");
        }

        if (actualCrc32 != expectedCrc32)
        {
            throw new InvalidDataException(
                $"The decoded yEnc CRC32 was {actualCrc32:x8}, but the trailer expected {expectedCrc32:x8}.");
        }
    }

    private static bool TryParseYencTrailerCrc32(
        ReadOnlySpan<byte> trailer,
        ReadOnlySpan<byte> fieldName,
        out uint crc32)
    {
        var position = 0;
        while (position < trailer.Length)
        {
            while (position < trailer.Length && IsAsciiWhitespace(trailer[position]))
            {
                position++;
            }

            var tokenStart = position;
            while (position < trailer.Length && !IsAsciiWhitespace(trailer[position]))
            {
                position++;
            }

            var token = trailer[tokenStart..position];
            var separator = token.IndexOf((byte)'=');
            if (separator <= 0 ||
                !AsciiEqualsIgnoreCase(token[..separator], fieldName))
            {
                continue;
            }

            var value = token[(separator + 1)..];
            return Utf8Parser.TryParse(value, out crc32, out var consumed, 'X') &&
                consumed == value.Length;
        }

        crc32 = 0;
        return false;
    }

    private static bool AsciiEqualsIgnoreCase(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            var leftValue = left[index];
            var rightValue = right[index];
            if (leftValue is >= (byte)'A' and <= (byte)'Z')
            {
                leftValue += (byte)('a' - 'A');
            }

            if (rightValue is >= (byte)'A' and <= (byte)'Z')
            {
                rightValue += (byte)('a' - 'A');
            }

            if (leftValue != rightValue)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private async Task ReadBodyToPipeAsync(
        PipeWriter writer,
        CancellationTokenSource operationCts,
        CancellationToken callerCancellationToken,
        Action<ArticleBodyResult>? onConnectionReadyAgain)
    {
        Exception? failure = null;
        try
        {
            if (_reader == null)
            {
                throw new UsenetNotConnectedException("The NNTP connection closed before the article body was read.");
            }

            var shouldWrite = true;
            long drainedBytes = 0;
            var cancellationToken = operationCts.Token;
            using var readTimeout = new CoalescedReadTimeout(
                cancellationToken, _options.ReadTimeout, _timeProvider);

            // Read lines until we encounter the termination sequence (single dot on a line)
            while (true)
            {
                ReadOnlyMemory<byte>? lineMemory;
                try
                {
                    lineMemory = await ReadLineBytesAsync(readTimeout).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                if (!lineMemory.HasValue)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                var line = lineMemory.Value.Span;

                // Check for NNTP termination sequence (single dot)
                if (line.Length == 1 && line[0] == (byte)'.')
                {
                    break;
                }

                if (!shouldWrite)
                {
                    drainedBytes += line.Length + 2;
                    if (drainedBytes > _options.AbandonedBodyDrainLimit)
                    {
                        throw new UsenetProtocolException(
                            "The abandoned NNTP body exceeded the configured drain limit.");
                    }

                    continue;
                }

                // NNTP escaping: Lines starting with ".." should have the first dot removed
                if (line.Length >= 2 && line[0] == (byte)'.' && line[1] == (byte)'.')
                {
                    line = line[1..];
                }

                // Copy protocol bytes directly so yEnc data never round-trips through UTF-16.
                var destination = writer.GetSpan(line.Length + 2);
                line.CopyTo(destination);
                destination[line.Length] = (byte)'\r';
                destination[line.Length + 1] = (byte)'\n';
                writer.Advance(line.Length + 2);

                // Flush periodically to make data available for reading
                var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (result.IsCompleted || result.IsCanceled)
                {
                    shouldWrite = false;
                }
            }
        }
        catch (OperationCanceledException e) when (callerCancellationToken.IsCancellationRequested)
        {
            failure = e;
            var drainFailure = await TryDrainBodyAsync().ConfigureAwait(false);
            if (drainFailure != null)
            {
                RecordBackgroundFailure(drainFailure);
            }
        }
        catch (Exception e)
        {
            failure = e;
            RecordBackgroundFailure(e);
        }
        finally
        {
            await writer.CompleteAsync(failure).ConfigureAwait(false);
            operationCts.Dispose();
            _commandLock.Release();
            try
            {
                onConnectionReadyAgain?.Invoke(
                    failure == null ? ArticleBodyResult.Retrieved : ArticleBodyResult.NotRetrieved);
            }
            catch
            {
                // User callbacks must not fault the unobserved background transfer task.
            }
        }
    }

    private async Task<Exception?> TryDrainBodyAsync()
    {
        try
        {
            using var drainCts = CreateOperationTokenSource(CancellationToken.None);
            using var readTimeout = new CoalescedReadTimeout(
                drainCts.Token, _options.ReadTimeout, _timeProvider);
            long drainedBytes = 0;

            while (true)
            {
                ReadOnlyMemory<byte>? line;
                try
                {
                    line = await ReadLineBytesAsync(readTimeout).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return new UsenetProtocolException(
                        "The NNTP connection closed while draining a cancelled body.");
                }

                if (!line.HasValue)
                {
                    return new UsenetProtocolException(
                        "The NNTP connection closed while draining a cancelled body.");
                }

                var bytes = line.Value.Span;
                if (bytes.Length == 1 && bytes[0] == (byte)'.')
                {
                    return null;
                }

                drainedBytes += bytes.Length + 2;
                if (drainedBytes > _options.AbandonedBodyDrainLimit)
                {
                    return new UsenetProtocolException(
                        "The cancelled NNTP body exceeded the configured drain limit.");
                }
            }
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private void RecordBackgroundFailure(Exception failure)
    {
        lock (_stateLock)
        {
            _backgroundException = ExceptionDispatchInfo.Capture(failure);
        }
    }
}
