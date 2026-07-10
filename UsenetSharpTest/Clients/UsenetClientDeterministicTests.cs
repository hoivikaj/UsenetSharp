using System.Text;
using UsenetSharp.Clients;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharpTest.Support;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public class UsenetClientDeterministicTests
{
    [Test]
    public async Task DateAsync_UsesScriptedServer()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            Assert.That(command, Is.EqualTo("DATE"));
            await writer.WriteLineAsync("111 20260709213000");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.DateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.ResponseCode, Is.EqualTo(111));
            Assert.That(response.DateTime, Is.EqualTo(
                new DateTimeOffset(2026, 7, 9, 21, 30, 0, TimeSpan.Zero)));
        });
    }

    [Test]
    public async Task SegmentId_WithCrLf_IsRejectedBeforeCommandIsSent()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<ArgumentException>(() =>
            client.StatAsync("safe@example.com\r\nQUIT", CancellationToken.None));
        Assert.That(server.Commands, Is.Empty);
    }

    [Test]
    public async Task Credentials_WithCrLf_AreRejectedBeforeCommandIsSent()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<ArgumentException>(() =>
            client.AuthenticateAsync("user\r\nQUIT", "password", CancellationToken.None));
        Assert.That(server.Commands, Is.Empty);
    }

    [Test]
    public async Task DateAsync_CancellationInterruptsResponseRead()
    {
        await using var server = new ScriptedNntpServer(async (_, _, cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.ThrowsAsync<OperationCanceledException>(() => client.DateAsync(cts.Token));
    }

    [Test]
    public async Task BodyAsync_DotUnstuffsAndReportsRetrieved()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            Assert.That(command, Is.EqualTo("BODY <article@example.com>"));
            await writer.WriteAsync("222 body follows\r\n..leading-dot\r\ncontent\r\n.\r\n");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);
        using var reader = new StreamReader(response.Stream!, Encoding.Latin1);
        var content = await reader.ReadToEndAsync();

        Assert.That(content, Is.EqualTo(".leading-dot\r\ncontent\r\n"));
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.Retrieved));
    }

    [Test]
    public async Task BodyAsync_PreservesLatin1BytesWithoutTranscoding()
    {
        var bodyCharacters = new[] { '\0', '\x01', '\x7f', '\x80', '\xff' };
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteAsync($"222 body follows\r\n{new string(bodyCharacters)}\r\n.\r\n"));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.BodyAsync("article@example.com", CancellationToken.None);
        using var body = new MemoryStream();
        await response.Stream!.CopyToAsync(body);

        Assert.That(body.ToArray(), Is.EqualTo(new byte[] { 0x00, 0x01, 0x7f, 0x80, 0xff, 0x0d, 0x0a }));
    }

    [Test]
    public async Task DecodedBodyAsync_DecodesLargeBodyAndProvidesHeaders()
    {
        var expected = Enumerable.Range(0, 180_000)
            .Select(index => (byte)((index * 31 + 7) % 256))
            .ToArray();
        int? column = 0;
        var encoded = RapidYencSharp.YencEncoder.EncodeEx(expected, ref column, 128, true);
        var wireEncoded = DotStuff(encoded);

        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("BODY", StringComparison.Ordinal))
            {
                await writer.WriteAsync(
                    $"222 body follows\r\n" +
                    $"=ybegin part=3 total=8 line=128 size=1440000 name=chunked.bin\r\n" +
                    "=ypart begin=360001 end=540000\r\n");
                await writer.WriteAsync(Encoding.Latin1.GetString(wireEncoded));
                await writer.WriteAsync("\r\n=yend size=180000 part=3\r\n.\r\n");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.DecodedBodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);
        var headers = await response.Stream!.GetYencHeadersAsync();
        using var decoded = new MemoryStream();
        await response.Stream.CopyToAsync(decoded);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.ToArray(), Is.EqualTo(expected));
            Assert.That(headers, Is.Not.Null);
            Assert.That(headers!.FileName, Is.EqualTo("chunked.bin"));
            Assert.That(headers.FileSize, Is.EqualTo(1_440_000));
            Assert.That(headers.PartNumber, Is.EqualTo(3));
            Assert.That(headers.TotalParts, Is.EqualTo(8));
            Assert.That(headers.PartOffset, Is.EqualTo(360_000));
            Assert.That(headers.PartSize, Is.EqualTo(180_000));
        });
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.Retrieved));

        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo((int)UsenetResponseType.DateAndTime));
    }

    [Test]
    public async Task DecodedBodyAsync_RawModeDotUnstuffsPayload()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteAsync(
                "222 body follows\r\n" +
                "=ybegin line=128 size=1 name=dot.bin\r\n" +
                "..\r\n" +
                "=yend size=1\r\n" +
                ".\r\n"));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.DecodedBodyAsync(
            "article@example.com", CancellationToken.None);
        using var decoded = new MemoryStream();
        await response.Stream!.CopyToAsync(decoded);

        Assert.That(decoded.ToArray(), Is.EqualTo(new byte[] { 0x04 }));
    }

    [Test]
    public async Task DecodedBodyAsync_TruncatedTransferFailsAndReportsNotRetrieved()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync(
                "222 body follows\r\n" +
                "=ybegin line=128 size=10 name=truncated.bin\r\n" +
                "encoded-without-terminator\r\n");
            throw new IOException("Close scripted connection.");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.DecodedBodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);

        Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await response.Stream!.CopyToAsync(Stream.Null));
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task DecodedBodyAsync_StalledTransferTimesOutDeterministically()
    {
        var timeProvider = new ManualTimeProvider();
        var readTimeout = TimeSpan.FromSeconds(1);
        await using var server = new ScriptedNntpServer(async (_, writer, cancellationToken) =>
        {
            await writer.WriteAsync(
                "222 body follows\r\n" +
                "=ybegin line=128 size=2 name=stalled.bin\r\n" +
                "k\r\n");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = readTimeout
        }, timeProvider);
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.DecodedBodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);
        _ = await response.Stream!.GetYencHeadersAsync();
        var copyTask = response.Stream.CopyToAsync(Stream.Null);
        timeProvider.Advance(readTimeout);

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await copyTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
    }

    [Test]
    public async Task DecodedBodyAsync_CallerCancellationDrainsAndReusesConnection()
    {
        var firstLineSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("BODY", StringComparison.Ordinal))
            {
                await writer.WriteAsync(
                    "222 body follows\r\n" +
                    "=ybegin line=128 size=2 name=cancel.bin\r\n" +
                    "k\r\n");
                firstLineSent.SetResult();
                await continueBody.Task;
                await writer.WriteAsync("l\r\n=yend size=2\r\n.\r\n");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(1),
            AbandonedBodyDrainLimit = 1024
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.DecodedBodyAsync(
            "article@example.com", completion.SetResult, cts.Token);
        var copyTask = response.Stream!.CopyToAsync(Stream.Null);
        await firstLineSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        continueBody.SetResult();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await copyTask);
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo((int)UsenetResponseType.DateAndTime));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task BodyAsync_TruncatedTransferFailsStreamAndReportsNotRetrieved()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("222 body follows\r\npartial\r\n");
            throw new IOException("Close scripted connection.");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);

        Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await response.Stream!.CopyToAsync(Stream.Null));
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        Assert.Multiple(() =>
        {
            Assert.That(client.IsConnected, Is.True);
            Assert.That(client.IsHealthy, Is.False);
        });
    }

    [Test]
    public async Task BodyAsync_StalledTransferTimesOutAndReportsNotRetrieved()
    {
        var timeProvider = new ManualTimeProvider();
        var readTimeout = TimeSpan.FromSeconds(1);
        await using var server = new ScriptedNntpServer(async (_, writer, cancellationToken) =>
        {
            await writer.WriteAsync("222 body follows\r\npartial");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = readTimeout
        }, timeProvider);
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);
        var copyTask = response.Stream!.CopyToAsync(Stream.Null);
        timeProvider.Advance(readTimeout);

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await copyTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
    }

    [Test]
    public async Task BodyAsync_ContinuousProgressBeyondReadTimeout_DoesNotTimeOut()
    {
        var timeProvider = new ManualTimeProvider();
        var bodyChunks = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        await using var server = new ScriptedNntpServer(async (_, writer, cancellationToken) =>
        {
            await writer.WriteAsync("222 body follows\r\n");
            foreach (var bodyChunk in bodyChunks)
            {
                await writer.WriteAsync(await bodyChunk.Task.WaitAsync(cancellationToken));
            }
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(1)
        }, timeProvider);
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.BodyAsync(
            "article@example.com", CancellationToken.None);
        using var reader = new StreamReader(response.Stream!, Encoding.Latin1);
        for (var index = 0; index < 3; index++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(750));
            bodyChunks[index].SetResult($"line-{index}\r\n");
            Assert.That(
                await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)),
                Is.EqualTo($"line-{index}"));
        }

        bodyChunks[3].SetResult(".\r\n");
        Assert.That(
            await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)),
            Is.Null);
    }

    [Test]
    public void Constructor_RejectsInvalidOptions()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UsenetClient(new UsenetClientOptions { ReadTimeout = TimeSpan.Zero }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UsenetClient(new UsenetClientOptions { AbandonedBodyDrainLimit = -1 }));
        });
    }

    [Test]
    public async Task BodyAsync_AbandonedBodyBeyondDrainLimitReportsNotRetrieved()
    {
        var continueBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("222 body follows\r\ninitial\r\n");
            await continueBody.Task;
            await writer.WriteAsync("after-dispose\r\n12345\r\n.\r\n");
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            AbandonedBodyDrainLimit = 4
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);
        await response.Stream!.DisposeAsync();
        continueBody.SetResult();

        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.DateAsync(CancellationToken.None));
    }

    [Test]
    public async Task BodyAsync_CallerCancellationDrainsAndReusesConnection()
    {
        var firstLineSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("BODY", StringComparison.Ordinal))
            {
                await writer.WriteAsync("222 body follows\r\nfirst\r\n");
                firstLineSent.SetResult();
                await continueBody.Task;
                await writer.WriteAsync("second\r\n.\r\n");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(1),
            AbandonedBodyDrainLimit = 1024
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync("article@example.com", completion.SetResult, cts.Token);
        var copyTask = response.Stream!.CopyToAsync(Stream.Null);
        await firstLineSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        cts.Cancel();
        continueBody.SetResult();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await copyTask);
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo((int)UsenetResponseType.DateAndTime));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task BodyAsync_CancelledBodyDrainTimesOutDeterministically()
    {
        var timeProvider = new ManualTimeProvider();
        var readTimeout = TimeSpan.FromSeconds(1);
        await using var server = new ScriptedNntpServer(async (_, writer, cancellationToken) =>
        {
            await writer.WriteAsync("222 body follows\r\nfirst\r\n");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = readTimeout,
            AbandonedBodyDrainLimit = 1024
        }, timeProvider);
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var callerCts = new CancellationTokenSource();
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync(
            "article@example.com", completion.SetResult, callerCts.Token);
        using var reader = new StreamReader(response.Stream!, Encoding.Latin1);
        Assert.That(await reader.ReadLineAsync(), Is.EqualTo("first"));
        var copyTask = reader.ReadToEndAsync();
        callerCts.Cancel();
        await timeProvider.WaitForCreatedTimerCountAsync(2, waitCts.Token);
        timeProvider.Advance(readTimeout);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await copyTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        Assert.ThrowsAsync<TimeoutException>(() =>
            client.DateAsync(CancellationToken.None));
    }

    [Test]
    public async Task OversizedResponseLine_IsRejected()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteLineAsync($"111 {new string('0', 70_000)}"));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<UsenetProtocolException>(() => client.DateAsync(CancellationToken.None));
    }

    [Test]
    public async Task HealthProperties_ReflectConnectionLifecycle()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        var client = new UsenetClient();
        Assert.Multiple(() =>
        {
            Assert.That(client.IsConnected, Is.False);
            Assert.That(client.IsHealthy, Is.False);
        });

        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(client.IsConnected, Is.True);
            Assert.That(client.IsHealthy, Is.True);
        });

        await client.DisposeAsync();
        Assert.Multiple(() =>
        {
            Assert.That(client.IsConnected, Is.False);
            Assert.That(client.IsHealthy, Is.False);
        });
    }

    [Test]
    public async Task CallsAfterDispose_ThrowObjectDisposedException()
    {
        var client = new UsenetClient();
        await client.DisposeAsync();

        Assert.ThrowsAsync<ObjectDisposedException>(() => client.DateAsync(CancellationToken.None));
    }

    [Test]
    public async Task ConcurrentDisposeAndDisposeAsync_AreSingleShot()
    {
        var client = new UsenetClient();

        await Task.WhenAll(
            Task.Run(client.Dispose),
            client.DisposeAsync().AsTask(),
            Task.Run(client.Dispose));

        Assert.ThrowsAsync<ObjectDisposedException>(() => client.DateAsync(CancellationToken.None));
    }

    [Test]
    public async Task Reconnect_WaitsForActiveBodyToFinish()
    {
        var finishBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("BODY", StringComparison.Ordinal))
            {
                await writer.WriteAsync("222 body follows\r\ncontent\r\n");
                await finishBody.Task;
                await writer.WriteAsync(".\r\n");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var response = await client.BodyAsync("article@example.com", CancellationToken.None);

        var reconnect = client.ConnectAsync(
            "127.0.0.1", server.Port, false, CancellationToken.None);
        await Task.Delay(50);
        Assert.That(reconnect.IsCompleted, Is.False);

        finishBody.SetResult();
        await response.Stream!.CopyToAsync(Stream.Null);
        await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task DisposeAsync_WithQueuedBody_StillReportsNotRetrieved()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, cancellationToken) =>
        {
            await writer.WriteAsync("222 body follows\r\npartial");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var activeCompletion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedCompletion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // First body holds the command lock; the second command queues behind it.
        _ = await client.BodyAsync(
            "active@example.com", activeCompletion.SetResult, CancellationToken.None);
        var queuedBody = client.BodyAsync(
            "queued@example.com", queuedCompletion.SetResult, CancellationToken.None);
        await Task.Delay(100);

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.ThrowsAsync<ObjectDisposedException>(() => queuedBody);
        Assert.That(await queuedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        Assert.That(await activeCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
    }

    [Test]
    public async Task DisposeAsync_CancelsActiveBodyWithoutDeadlock()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, cancellationToken) =>
        {
            await writer.WriteAsync("222 body follows\r\npartial");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var response = await client.BodyAsync("article@example.com", CancellationToken.None);

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await response.Stream!.CopyToAsync(Stream.Null));
    }

    private static byte[] DotStuff(ReadOnlySpan<byte> data)
    {
        using var stuffed = new MemoryStream(data.Length);
        var atLineStart = true;
        foreach (var value in data)
        {
            if (atLineStart && value == (byte)'.')
            {
                stuffed.WriteByte(value);
            }

            stuffed.WriteByte(value);
            atLineStart = value == (byte)'\n';
        }

        return stuffed.ToArray();
    }
}
