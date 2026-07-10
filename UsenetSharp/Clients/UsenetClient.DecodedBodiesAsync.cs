using System.IO.Pipelines;
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

        var segments = new SegmentId[segmentIds.Count];
        var validatedSegmentIds = new string[segmentIds.Count];
        for (var index = 0; index < segmentIds.Count; index++)
        {
            segments[index] = segmentIds[index];
            validatedSegmentIds[index] = ValidateSegmentId(segmentIds[index]);
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
        var commandsWritten = 0;
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            using (var operationCts = CreateOperationTokenSource(cancellationToken))
            {
                foreach (var segmentId in validatedSegmentIds)
                {
                    await WriteLineAsync($"BODY <{segmentId}>".AsMemory(), operationCts.Token)
                        .ConfigureAwait(false);
                    commandsWritten++;
                }
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
            if (commandsWritten > 0)
            {
                RecordBackgroundFailure(exception);
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
                    RecordBackgroundFailure(drainFailure);
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
                RecordBackgroundFailure(drainFailure);
            }

            completionResult = drainFailure == null
                ? ArticleBodyResult.Cancelled
                : ArticleBodyResult.NotRetrieved;
        }
        catch (Exception exception)
        {
            failure = exception;
            completionResult = ArticleBodyResult.NotRetrieved;
            RecordBackgroundFailure(exception);
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
