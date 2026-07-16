using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    /// <summary>
    /// Pipelines decoded BODY commands and returns their ordered response tasks.
    /// </summary>
    /// <remarks>
    /// Consume or dispose each response stream before awaiting the next response. Later responses
    /// remain blocked by bounded backpressure until earlier streams are drained.
    /// </remarks>
    public Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken)
    {
        return DecodedBodiesAsync(segmentIds, null, cancellationToken);
    }

    /// <summary>
    /// Pipelines decoded BODY commands and reports when the complete batch releases the connection.
    /// </summary>
    /// <remarks>
    /// Consume or dispose each response stream before awaiting the next response. Later responses
    /// remain blocked by bounded backpressure until earlier streams are drained. The completion
    /// callback reports <see cref="ArticleBodyResult.NotFound"/> for clean 430 responses,
    /// <see cref="ArticleBodyResult.Cancelled"/> after a successfully drained cancellation, and
    /// <see cref="ArticleBodyResult.NotRetrieved"/> only when the connection is unsafe to reuse.
    /// </remarks>
    public async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException("At least one segment ID is required.", nameof(segmentIds));
        }

        if (segmentIds.Count > _options.MaxPipelineDepth)
        {
            throw new ArgumentException(
                $"Batch exceeds MaxPipelineDepth ({_options.MaxPipelineDepth}); " +
                "split into smaller batches to avoid TCP-window pipeline deadlock (RFC 3977 §3.5).",
                nameof(segmentIds));
        }

        var segments = new SegmentId[segmentIds.Count];
        for (var index = 0; index < segmentIds.Count; index++)
        {
            segments[index] = segmentIds[index];
            ValidateSegmentId(segmentIds[index]);
        }

        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            InvokeBatchCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
            throw;
        }

        var pumpStarted = false;
        var writeStarted = false;
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            using (var operationCts = CreateOperationTokenSource(cancellationToken))
            {
                // Bytes may reach the wire from here on (RFC 3977 §3.5).
                writeStarted = true;
                await WritePipelinedBodyCommandsAsync(segments, operationCts.Token)
                    .ConfigureAwait(false);
            }

            var completions = segments
                .Select(_ => new TaskCompletionSource<UsenetDecodedBodyResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
            var responses = completions.Select(completion => completion.Task).ToArray();

            pumpStarted = true;
            _ = ProcessDecodedBodyBatchAsync(
                segments,
                completions,
                cancellationToken,
                onConnectionReadyAgain);

            return new UsenetDecodedBodyBatch { Responses = responses };
        }
        catch (Exception exception)
        {
            if (writeStarted)
            {
                RecordConnectionFailure(exception);
            }

            throw;
        }
        finally
        {
            if (!pumpStarted)
            {
                _commandLock.Release();
                InvokeBatchCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
            }
        }
    }

    private async ValueTask WritePipelinedBodyCommandsAsync(
        IReadOnlyList<SegmentId> segments,
        CancellationToken cancellationToken)
    {
        var totalLength = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            // "BODY <id>\r\n"
            totalLength += 4 + 1 + 1 + segments[index].Value.Length + 1 + 2;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var written = 0;
            for (var index = 0; index < segments.Count; index++)
            {
                written += FormatBodyCommand(buffer.AsSpan(written), segments[index]);
            }

            await WriteCommandAsync(buffer.AsMemory(0, written), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int FormatBodyCommand(Span<byte> destination, SegmentId segmentId)
    {
        var written = Encoding.Latin1.GetBytes("BODY <", destination);
        written += Encoding.Latin1.GetBytes(segmentId.Value, destination[written..]);
        destination[written++] = (byte)'>';
        destination[written++] = (byte)'\r';
        destination[written++] = (byte)'\n';
        return written;
    }

    private async Task ProcessDecodedBodyBatchAsync(
        IReadOnlyList<SegmentId> segmentIds,
        IReadOnlyList<TaskCompletionSource<UsenetDecodedBodyResponse>> completions,
        CancellationToken callerCancellationToken,
        Action<ArticleBodyResult>? onConnectionReadyAgain)
    {
        Exception? failure = null;
        var completionResult = ArticleBodyResult.Retrieved;
        var nextResponseIndex = 0;

        try
        {
            for (; nextResponseIndex < segmentIds.Count; nextResponseIndex++)
            {
                var segmentId = segmentIds[nextResponseIndex];
                var operationCts = CreateOperationTokenSource(callerCancellationToken);
                string? response;
                int responseCode;
                try
                {
                    response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
                    responseCode = ParseResponseCode(response);
                }
                catch
                {
                    operationCts.Dispose();
                    throw;
                }

                if (responseCode != (int)UsenetResponseType.ArticleRetrievedBodyFollows)
                {
                    await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                        .ConfigureAwait(false);
                    completionResult =
                        responseCode == (int)UsenetResponseType.NoArticleWithThatMessageId &&
                        completionResult != ArticleBodyResult.NotRetrieved
                            ? ArticleBodyResult.NotFound
                            : ArticleBodyResult.NotRetrieved;
                    completions[nextResponseIndex].TrySetResult(new UsenetDecodedBodyResponse
                    {
                        SegmentId = segmentId,
                        ResponseCode = responseCode,
                        ResponseMessage = response!,
                        Stream = null
                    });
                    operationCts.Dispose();
                    continue;
                }

                var pipe = new Pipe(new PipeOptions(
                    pauseWriterThreshold: 1024 * 1024,
                    resumeWriterThreshold: 512 * 1024));
                var headersCompletion =
                    new TaskCompletionSource<UsenetYencHeader?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                completions[nextResponseIndex].TrySetResult(new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    Stream = new YencStream(pipe.Reader.AsStream(), headersCompletion.Task)
                });

                var bodyReadResult = await ReadDecodedBodyToPipeAsync(
                        pipe.Writer,
                        headersCompletion,
                        operationCts,
                        callerCancellationToken,
                        onConnectionReadyAgain: null,
                        releaseCommandLock: false)
                    .ConfigureAwait(false);
                if (bodyReadResult.Failure == null)
                {
                    continue;
                }

                failure = bodyReadResult.Failure;
                nextResponseIndex++;
                var drainFailure = await TryDrainPipelinedBodiesAsync(
                        segmentIds.Count - nextResponseIndex)
                    .ConfigureAwait(false);
                if (drainFailure != null)
                {
                    RecordConnectionFailure(drainFailure);
                }

                completionResult =
                    bodyReadResult.Failure is OperationCanceledException &&
                    callerCancellationToken.IsCancellationRequested &&
                    bodyReadResult.ConnectionReusable &&
                    drainFailure == null
                        ? ArticleBodyResult.Cancelled
                        : ArticleBodyResult.NotRetrieved;
                break;
            }
        }
        catch (OperationCanceledException exception) when (callerCancellationToken.IsCancellationRequested)
        {
            failure = exception;
            var drainFailure = await TryDrainPipelinedBodiesAsync(
                    segmentIds.Count - nextResponseIndex)
                .ConfigureAwait(false);
            if (drainFailure != null)
            {
                RecordConnectionFailure(drainFailure);
            }

            completionResult = drainFailure == null
                ? ArticleBodyResult.Cancelled
                : ArticleBodyResult.NotRetrieved;
        }
        catch (Exception exception)
        {
            failure = exception;
            completionResult = ArticleBodyResult.NotRetrieved;
            RecordConnectionFailure(exception);
        }
        finally
        {
            if (failure != null)
            {
                for (var index = nextResponseIndex; index < completions.Count; index++)
                {
                    completions[index].TrySetException(failure);
                }
            }

            _commandLock.Release();
            InvokeBatchCallback(
                onConnectionReadyAgain,
                completionResult);
        }
    }

    private async Task<Exception?> TryDrainPipelinedBodiesAsync(int responseCount)
    {
        try
        {
            for (var index = 0; index < responseCount; index++)
            {
                using var operationCts = CreateOperationTokenSource(CancellationToken.None);
                var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
                var responseCode = ParseResponseCode(response);
                if (responseCode != (int)UsenetResponseType.ArticleRetrievedBodyFollows)
                {
                    if (IsMultiLineCode(responseCode))
                    {
                        var unexpectedDrain = await TryDrainBodyAsync().ConfigureAwait(false);
                        if (unexpectedDrain != null)
                        {
                            return unexpectedDrain;
                        }
                    }

                    continue;
                }

                var bodyFailure = await TryDrainBodyAsync().ConfigureAwait(false);
                if (bodyFailure != null)
                {
                    return bodyFailure;
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void InvokeBatchCallback(
        Action<ArticleBodyResult>? callback,
        ArticleBodyResult result)
    {
        try
        {
            callback?.Invoke(result);
        }
        catch
        {
            // User callbacks must not fault command setup or the background pump.
        }
    }
}
