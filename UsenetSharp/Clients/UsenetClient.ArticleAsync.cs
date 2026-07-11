using System.IO.Pipelines;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public Task<UsenetArticleResponse> ArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return ArticleAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetArticleResponse> ArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ValidateSegmentId(segmentId);
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

            // Send ARTICLE command with message-id
            await WriteMessageIdCommandAsync("ARTICLE", segmentId, operationCts.Token)
                .ConfigureAwait(false);
            var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - head and body follow
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            {
                // Parse headers
                var headers = await ParseArticleHeadersAsync(operationCts.Token).ConfigureAwait(false);

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
                return new UsenetArticleResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    ArticleHeaders = headers,
                    Stream = pipe.Reader.AsStream(),
                };
            }

            return new UsenetArticleResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                Stream = null,
                ArticleHeaders = null
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

    private async Task<UsenetArticleHeader> ParseArticleHeadersAsync(
        CancellationToken cancellationToken,
        bool allowDotTerminator = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentHeaderName = null;
        var currentHeaderValue = new StringBuilder();
        var totalHeaderBytes = 0;
        var headerCount = 0;

        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line == null)
            {
                throw new UsenetProtocolException("Invalid NNTP response: missing article headers.");
            }

            // Empty line signals end of headers
            if (line == ".")
            {
                if (allowDotTerminator)
                {
                    if (currentHeaderName != null)
                    {
                        headers[currentHeaderName] = currentHeaderValue.ToString().Trim();
                    }

                    break;
                }

                throw new UsenetProtocolException(
                    "Invalid NNTP response: article headers were not followed by a blank line.");
            }

            if (string.IsNullOrEmpty(line))
            {
                // Save the last header if any
                if (currentHeaderName != null)
                {
                    headers[currentHeaderName] = currentHeaderValue.ToString().Trim();
                }

                break;
            }

            totalHeaderBytes += Encoding.Latin1.GetByteCount(line) + 2;
            if (totalHeaderBytes > 256 * 1024)
            {
                throw new UsenetProtocolException("NNTP article headers exceeded the 256 KiB limit.");
            }

            // Check if this is a continuation line (starts with whitespace)
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                // Append to current header value
                if (currentHeaderName != null)
                {
                    currentHeaderValue.Append(' ');
                    currentHeaderValue.Append(line.Trim());
                }
            }
            else
            {
                // Save the previous header if any
                if (currentHeaderName != null)
                {
                    headers[currentHeaderName] = currentHeaderValue.ToString().Trim();
                }

                // Parse new header: "Name: Value"
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    headerCount++;
                    if (headerCount > 256)
                    {
                        throw new UsenetProtocolException("NNTP article contained more than 256 headers.");
                    }

                    currentHeaderName = line.Substring(0, colonIndex).Trim();
                    currentHeaderValue.Clear();

                    // Get value after colon
                    if (colonIndex + 1 < line.Length)
                    {
                        currentHeaderValue.Append(line.Substring(colonIndex + 1).Trim());
                    }
                }
            }
        }

        return new UsenetArticleHeader
        {
            Headers = headers
        };
    }
}
