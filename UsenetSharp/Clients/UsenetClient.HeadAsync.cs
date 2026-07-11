using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateSegmentId(segmentId);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            using var operationCts = CreateOperationTokenSource(cancellationToken);

            // Send HEAD command with message-id
            await WriteMessageIdCommandAsync("HEAD", segmentId, operationCts.Token)
                .ConfigureAwait(false);
            var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - head follows (multi-line)
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedHeadFollows)
            {
                // Parse headers
                var headers = await ParseArticleHeadersAsync(operationCts.Token, allowDotTerminator: true)
                    .ConfigureAwait(false);

                return new UsenetHeadResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    ArticleHeaders = headers
                };
            }

            return new UsenetHeadResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                ArticleHeaders = null
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
