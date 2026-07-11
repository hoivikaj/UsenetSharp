using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
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

            // Send STAT command with message-id
            await WriteMessageIdCommandAsync("STAT", segmentId, operationCts.Token)
                .ConfigureAwait(false);
            var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
            var responseCode = ParseResponseCode(response);

            return new UsenetStatResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                ArticleExists = responseCode == (int)UsenetResponseType.ArticleExists,
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
