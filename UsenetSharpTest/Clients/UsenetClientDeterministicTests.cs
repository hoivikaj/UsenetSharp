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
    public async Task CallsAfterDispose_ThrowObjectDisposedException()
    {
        var client = new UsenetClient();
        await client.DisposeAsync();

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
}
