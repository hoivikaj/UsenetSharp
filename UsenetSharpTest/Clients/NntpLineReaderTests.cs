using System.Text;
using UsenetSharp.Clients;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public class NntpLineReaderTests
{
    [Test]
    public async Task ReadLineBytesAsync_CancelledRefillDoesNotReplayConsumedBuffer()
    {
        await using var stream = new CancelledRefillStream(
            "first response\r\n",
            "second response\r\n");
        using var reader = new NntpLineReader(stream);

        var first = await reader.ReadLineAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = reader.ReadLineAsync(cancellation.Token).AsTask();
        await stream.RefillStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await cancelledRead);
        var second = await reader.ReadLineAsync(CancellationToken.None);

        Assert.That(first, Is.EqualTo("first response"));
        Assert.That(second, Is.EqualTo("second response"));
    }

    private sealed class CancelledRefillStream(
        string firstResponse,
        string secondResponse) : Stream
    {
        private readonly byte[] _firstResponse = Encoding.ASCII.GetBytes(firstResponse);
        private readonly byte[] _secondResponse = Encoding.ASCII.GetBytes(secondResponse);
        private readonly TaskCompletionSource _refillStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public Task RefillStarted => _refillStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            switch (Interlocked.Increment(ref _readCount))
            {
                case 1:
                    _firstResponse.AsSpan().CopyTo(buffer.Span);
                    return _firstResponse.Length;
                case 2:
                    _refillStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                case 3:
                    _secondResponse.AsSpan().CopyTo(buffer.Span);
                    return _secondResponse.Length;
                default:
                    return 0;
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
