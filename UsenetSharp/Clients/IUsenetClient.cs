using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public interface IUsenetClient
{
    bool IsConnected { get; }

    bool IsHealthy { get; }

    Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken);

    Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken);

    Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetBodyResponse> BodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetBodyResponse> BodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pipelines decoded BODY commands and returns their ordered response tasks.
    /// </summary>
    /// <remarks>
    /// Consume or dispose each response stream before awaiting the next response. Later responses
    /// remain blocked by bounded backpressure until earlier streams are drained.
    /// </remarks>
    Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken)
    {
        return DecodedBodiesAsync(segmentIds, null, cancellationToken);
    }

    /// <summary>
    /// Pipelines decoded BODY commands and reports when the complete batch releases the connection.
    /// </summary>
    /// <remarks>
    /// Consume or dispose each response stream before awaiting the next response. Later responses
    /// remain blocked by bounded backpressure until earlier streams are drained. The completion
    /// callback distinguishes clean not-found and drained-cancellation outcomes from failures that
    /// make the connection unsafe to reuse.
    /// </remarks>
    Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"{GetType().Name} does not support pipelined decoded BODY commands.");
    }

    Task<UsenetArticleResponse> ArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetArticleResponse> ArticleAsync
        (SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken);

    Task WaitForReadyAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends QUIT and closes the connection after the server acknowledges (RFC 3977 §5.4).
    /// </summary>
    Task<UsenetResponse> QuitAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"{GetType().Name} does not support QUIT.");
    }

    /// <summary>
    /// Issues CAPABILITIES and returns the advertised capability labels (RFC 3977 §5.2).
    /// </summary>
    Task<UsenetCapabilitiesResponse> CapabilitiesAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"{GetType().Name} does not support CAPABILITIES.");
    }

    /// <summary>
    /// Issues MODE READER for mode-switching servers (RFC 3977 §5.3).
    /// </summary>
    Task<UsenetResponse> ModeReaderAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"{GetType().Name} does not support MODE READER.");
    }

    /// <summary>
    /// Probes a segment's yEnc headers via BODY without delivering the body.
    /// </summary>
    Task<UsenetYencHeaderResponse> YencHeadersAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        return YencHeadersAsync(
            segmentId, ConnectionReleasePolicy.DrainToReuse, cancellationToken);
    }

    /// <summary>
    /// Probes a segment's yEnc headers via BODY, releasing the connection
    /// according to <paramref name="releasePolicy"/>.
    /// </summary>
    Task<UsenetYencHeaderResponse> YencHeadersAsync(
        SegmentId segmentId, ConnectionReleasePolicy releasePolicy,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"{GetType().Name} does not support yEnc header probes.");
    }
}
