using System.Reflection;
using System.Security.Cryptography.X509Certificates;
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

    [TestCase("111 2023121514302")]
    [TestCase("111 +2031215143022")]
    [TestCase("111 20231315143022")]
    public async Task DateAsync_Malformed111Payload_ThrowsProtocolException(string malformed)
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteLineAsync(malformed));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<UsenetProtocolException>(() => client.DateAsync(CancellationToken.None));
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task DateAsync_Non111Response_LeavesDateTimeNull()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteLineAsync("502 Permission denied"));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.DateAsync(CancellationToken.None);
        Assert.That(response.ResponseCode, Is.EqualTo(502));
        Assert.That(response.DateTime, Is.Null);
        Assert.That(client.IsHealthy, Is.True);
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
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task SegmentId_LengthBounds_MatchRfc5536InteropLimit()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command == "QUIT")
            {
                await writer.WriteLineAsync("205 Connection closing");
                return;
            }

            if (command.StartsWith("STAT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("223 0 <id>");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var accepted = new string('a', 236) + "@example.com"; // 248 chars
        Assert.That(accepted.Length, Is.EqualTo(248));
        var response = await client.StatAsync(accepted, CancellationToken.None);
        Assert.That(response.ArticleExists, Is.True);

        var rejected = accepted + "x";
        Assert.ThrowsAsync<ArgumentException>(() =>
            client.StatAsync(rejected, CancellationToken.None));
        Assert.That(client.IsHealthy, Is.True);
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
    public async Task AuthenticateAsync_RequireTls_RejectsPlaintextBeforeWriting()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            RequireTlsForAuthentication = true
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AuthenticateAsync("user", "pass", CancellationToken.None));
        Assert.That(server.Commands, Is.Empty);
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task AuthenticateAsync_RejectsOversizedAndSpacedUsername()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<ArgumentException>(() =>
            client.AuthenticateAsync(new string('u', 497), "pass", CancellationToken.None));
        Assert.ThrowsAsync<ArgumentException>(() =>
            client.AuthenticateAsync("john smith", "pass", CancellationToken.None));
        Assert.That(server.Commands, Is.Empty);
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task AuthenticateAsync_Accepts496CharArgsAndPasswordWithSpace()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("AUTHINFO USER", StringComparison.Ordinal))
            {
                Assert.That(Encoding.Latin1.GetByteCount(command) + 2, Is.LessThanOrEqualTo(512));
                await writer.WriteLineAsync("381 Password required");
            }
            else if (command.StartsWith("AUTHINFO PASS", StringComparison.Ordinal))
            {
                Assert.That(command, Does.Contain("pass word"));
                await writer.WriteLineAsync("281 Authentication accepted");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.AuthenticateAsync(
            new string('u', 496), "pass word", CancellationToken.None);
        Assert.That(response.ResponseCode, Is.EqualTo(281));
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
    public async Task BodyAsync_LargeMultilineBodyPreservesBytes()
    {
        var expected = new StringBuilder();
        var wire = new StringBuilder("222 body follows\r\n");
        for (var index = 0; index < 1_000; index++)
        {
            var line = index % 10 == 0
                ? $".dot-prefixed-{index:D4}-{new string('x', 80)}"
                : $"line-{index:D4}-{new string('x', 90)}";
            expected.Append(line).Append("\r\n");
            if (line[0] == '.')
            {
                wire.Append('.');
            }

            wire.Append(line).Append("\r\n");
        }

        wire.Append(".\r\n");
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteAsync(wire.ToString()));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.BodyAsync("article@example.com", CancellationToken.None);
        using var reader = new StreamReader(response.Stream!, Encoding.Latin1);

        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo(expected.ToString()));
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
    public async Task DecodedBodyAsync_WithValidMultipartCrc32_Succeeds()
    {
        var expected = Enumerable.Range(0, 180_000)
            .Select(index => (byte)((index * 17 + 3) % 256))
            .ToArray();
        var crc32 = RapidYencSharp.Crc32.Compute(expected);
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await WriteYencArticleAsync(
                writer,
                expected,
                $"size={expected.Length} crc32=00000000\tPCRC32={crc32:X8} part=2",
                multipart: true));
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            CrcValidation = YencCrcValidationMode.Require
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.DecodedBodyAsync(
            "article@example.com", CancellationToken.None);
        using var decoded = new MemoryStream();
        await response.Stream!.CopyToAsync(decoded);

        Assert.That(decoded.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task DecodedBodyAsync_WithInvalidCrc32_Fails()
    {
        var expected = Encoding.ASCII.GetBytes("crc validation failure");
        var incorrectCrc32 = RapidYencSharp.Crc32.Compute(expected) ^ 1;
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await WriteYencArticleAsync(
                writer, expected, $"size={expected.Length} crc32={incorrectCrc32:x8}"));
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            CrcValidation = YencCrcValidationMode.Require
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.DecodedBodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);

        var exception = Assert.ThrowsAsync<InvalidDataException>(async () =>
            await response.Stream!.CopyToAsync(Stream.Null));
        Assert.That(exception!.Message, Does.Contain("trailer expected"));
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
    }

    [Test]
    public async Task DecodedBodyAsync_WithMissingCrc32_Fails()
    {
        var expected = Encoding.ASCII.GetBytes("missing crc");
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await WriteYencArticleAsync(writer, expected, $"size={expected.Length}"));
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            CrcValidation = YencCrcValidationMode.Require
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.DecodedBodyAsync(
            "article@example.com", CancellationToken.None);

        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await response.Stream!.CopyToAsync(Stream.Null));
    }

    [Test]
    public async Task DecodedBodyAsync_Crc32ValidationDisabledByDefault_IgnoresMismatch()
    {
        var expected = Encoding.ASCII.GetBytes("unchecked crc");
        var incorrectCrc32 = RapidYencSharp.Crc32.Compute(expected) ^ 1;
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await WriteYencArticleAsync(
                writer, expected, $"size={expected.Length} crc32={incorrectCrc32:x8}"));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.DecodedBodyAsync(
            "article@example.com", CancellationToken.None);
        using var decoded = new MemoryStream();
        await response.Stream!.CopyToAsync(decoded);

        Assert.That(decoded.ToArray(), Is.EqualTo(expected));
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
            Is.EqualTo(ArticleBodyResult.Cancelled));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo((int)UsenetResponseType.DateAndTime));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task DecodedBodiesAsync_SendsCommandsAheadAndStreamsResponsesInOrder()
    {
        var expected = new[]
        {
            Array.Empty<byte>(),
            Array.Empty<byte>()
        };
        var commandsReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ScriptedNntpServer.StartConnectionScript(
            async (reader, writer, cancellationToken) =>
            {
                Assert.That(
                    await reader.ReadLineAsync(cancellationToken),
                    Is.EqualTo("BODY <first@example.com>"));
                Assert.That(
                    await reader.ReadLineAsync(cancellationToken),
                    Is.EqualTo("BODY <second@example.com>"));
                commandsReceived.SetResult();

                await WriteSimpleYencArticleAsync(
                    writer, expected[0], $"size={expected[0].Length}", "first.bin");
                await WriteSimpleYencArticleAsync(
                    writer, expected[1], $"size={expected[1].Length}", "second.bin");

                Assert.That(
                    await reader.ReadLineAsync(cancellationToken),
                    Is.EqualTo("DATE"));
                await writer.WriteLineAsync("111 20260709213000");
            });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var callbackCount = 0;
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var batch = await client.DecodedBodiesAsync(
            new SegmentId[] { "first@example.com", "second@example.com" },
            result =>
            {
                Interlocked.Increment(ref callbackCount);
                completion.TrySetResult(result);
            },
            CancellationToken.None);
        await commandsReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var index = 0; index < batch.Responses.Count; index++)
        {
            var response = await batch.Responses[index];
            Assert.That(response.SegmentId, Is.EqualTo(
                index == 0 ? "first@example.com" : "second@example.com"));
            var headers = await response.Stream!.GetYencHeadersAsync();
            Assert.That(headers!.FileName, Is.EqualTo(
                index == 0 ? "first.bin" : "second.bin"));
            using var decoded = new MemoryStream();
            await response.Stream.CopyToAsync(decoded);
            Assert.That(decoded.ToArray(), Is.EqualTo(expected[index]));
        }

        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.Retrieved));
        Assert.That(callbackCount, Is.EqualTo(1));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo((int)UsenetResponseType.DateAndTime));
    }

    [Test]
    public async Task DecodedBodiesAsync_MissingBodyContinuesInProtocolOrder()
    {
        var expected = Array.Empty<byte>();
        await using var server = ScriptedNntpServer.StartConnectionScript(
            async (reader, writer, cancellationToken) =>
            {
                _ = await reader.ReadLineAsync(cancellationToken);
                _ = await reader.ReadLineAsync(cancellationToken);
                await writer.WriteLineAsync("430 no article with that message-id");
                await WriteSimpleYencArticleAsync(writer, expected, $"size={expected.Length}");
                Assert.That(
                    await reader.ReadLineAsync(cancellationToken),
                    Is.EqualTo("DATE"));
                await writer.WriteLineAsync("111 20260709213000");
            });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var batch = await client.DecodedBodiesAsync(
            new SegmentId[] { "missing@example.com", "available@example.com" },
            completion.SetResult,
            CancellationToken.None);
        var missing = await batch.Responses[0];
        var available = await batch.Responses[1];
        using var decoded = new MemoryStream();
        await available.Stream!.CopyToAsync(decoded);

        Assert.Multiple(() =>
        {
            Assert.That(missing.ResponseType,
                Is.EqualTo(UsenetResponseType.NoArticleWithThatMessageId));
            Assert.That(missing.Stream, Is.Null);
            Assert.That(decoded.ToArray(), Is.EqualTo(expected));
        });
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotFound));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo((int)UsenetResponseType.DateAndTime));
    }

    [Test]
    public async Task DecodedBodiesAsync_TruncatedBodyFailsRemainingBatch()
    {
        await using var server = ScriptedNntpServer.StartConnectionScript(
            async (reader, writer, cancellationToken) =>
            {
                _ = await reader.ReadLineAsync(cancellationToken);
                _ = await reader.ReadLineAsync(cancellationToken);
                await writer.WriteAsync(
                    "222 body follows\r\n" +
                    "=ybegin line=128 size=10 name=truncated.bin\r\n" +
                    "encoded-without-terminator\r\n");
            });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var batch = await client.DecodedBodiesAsync(
            new SegmentId[] { "truncated@example.com", "pending@example.com" },
            completion.SetResult,
            CancellationToken.None);
        var truncated = await batch.Responses[0];

        Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await truncated.Stream!.CopyToAsync(Stream.Null));
        Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await batch.Responses[1]);
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task DecodedBodiesAsync_CancellationDrainsBatchAndReusesConnection()
    {
        var partialBodySent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBody = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ScriptedNntpServer.StartConnectionScript(
            async (reader, writer, cancellationToken) =>
            {
                _ = await reader.ReadLineAsync(cancellationToken);
                _ = await reader.ReadLineAsync(cancellationToken);
                await writer.WriteAsync(
                    "222 body follows\r\n" +
                    "=ybegin line=128 size=2 name=first.bin\r\n" +
                    "k\r\n");
                partialBodySent.SetResult();
                await continueBody.Task.WaitAsync(cancellationToken);
                await writer.WriteAsync("l\r\n=yend size=2\r\n.\r\n");
                await WriteSimpleYencArticleAsync(writer, [3, 4], "size=2");
                Assert.That(
                    await reader.ReadLineAsync(cancellationToken),
                    Is.EqualTo("BODY <after-first@example.com>"));
                Assert.That(
                    await reader.ReadLineAsync(cancellationToken),
                    Is.EqualTo("BODY <after-second@example.com>"));
                await WriteSimpleYencArticleAsync(
                    writer, [5, 6], "size=2", "after-first.bin");
                await WriteSimpleYencArticleAsync(
                    writer, [7, 8], "size=2", "after-second.bin");
                Assert.That(
                    await reader.ReadLineAsync(cancellationToken),
                    Is.EqualTo("DATE"));
                await writer.WriteLineAsync("111 20260709213000");
            });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(1),
            AbandonedBodyDrainLimit = 1024
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var callbackCount = 0;
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var batch = await client.DecodedBodiesAsync(
            new SegmentId[] { "first@example.com", "second@example.com" },
            result =>
            {
                Interlocked.Increment(ref callbackCount);
                completion.TrySetResult(result);
            },
            cts.Token);
        var first = await batch.Responses[0];
        var copyTask = first.Stream!.CopyToAsync(Stream.Null);
        await partialBodySent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        continueBody.SetResult();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await copyTask);
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await batch.Responses[1]);
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.Cancelled));
        Assert.That(callbackCount, Is.EqualTo(1));

        var nextBatch = await client.DecodedBodiesAsync(
            new SegmentId[] { "after-first@example.com", "after-second@example.com" },
            CancellationToken.None);
        for (var index = 0; index < nextBatch.Responses.Count; index++)
        {
            var nextResponse = await nextBatch.Responses[index];
            Assert.That(
                nextResponse.SegmentId,
                Is.EqualTo(index == 0
                    ? "after-first@example.com"
                    : "after-second@example.com"));
            var headers = await nextResponse.Stream!.GetYencHeadersAsync();
            Assert.That(
                headers!.FileName,
                Is.EqualTo(index == 0 ? "after-first.bin" : "after-second.bin"));
            await nextResponse.Stream.CopyToAsync(Stream.Null);
        }

        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo((int)UsenetResponseType.DateAndTime));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task DecodedBodiesAsync_CancellationWithFailedCurrentDrainReportsNotRetrieved()
    {
        var partialBodySent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeConnection = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ScriptedNntpServer.StartConnectionScript(
            async (reader, writer, cancellationToken) =>
            {
                _ = await reader.ReadLineAsync(cancellationToken);
                await writer.WriteAsync(
                    "222 body follows\r\n" +
                    "=ybegin line=128 size=2 name=first.bin\r\n" +
                    "k\r\n");
                partialBodySent.SetResult();
                await closeConnection.Task.WaitAsync(cancellationToken);
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

        var batch = await client.DecodedBodiesAsync(
            new SegmentId[] { "first@example.com" },
            completion.SetResult,
            cts.Token);
        var response = await batch.Responses[0];
        var copyTask = response.Stream!.CopyToAsync(Stream.Null);
        await partialBodySent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        closeConnection.SetResult();

        Assert.CatchAsync<OperationCanceledException>(async () => await copyTask);
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
        Assert.That(client.IsHealthy, Is.False);
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
    public async Task HeadAsync_DuplicateHeaders_PreservesAllInOrderWithFirstWins()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command == "QUIT")
            {
                await writer.WriteLineAsync("205 Connection closing");
                return;
            }

            await writer.WriteAsync(
                "221 0 <article@example.com>\r\n" +
                "X-Trace: first\r\n" +
                "X-Trace: second\r\n" +
                ".\r\n");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var head = await client.HeadAsync("article@example.com", CancellationToken.None);
        Assert.That(head.ArticleHeaders!.Headers["X-Trace"], Is.EqualTo("first"));
        Assert.That(head.ArticleHeaders.AllHeaders.Count, Is.EqualTo(2));
        Assert.That(head.ArticleHeaders.AllHeaders[0].Value, Is.EqualTo("first"));
        Assert.That(head.ArticleHeaders.AllHeaders[1].Value, Is.EqualTo("second"));
    }

    [Test]
    public async Task CapabilitiesAsync_ParsesLabelsAndDotUnstuffs()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command == "QUIT")
            {
                await writer.WriteLineAsync("205 Connection closing");
                return;
            }

            Assert.That(command, Is.EqualTo("CAPABILITIES"));
            await writer.WriteAsync(
                "101 Capability list follows\r\n" +
                "VERSION 2\r\n" +
                "READER\r\n" +
                "..STUFFED\r\n" +
                "IHAVE\r\n" +
                ".\r\n");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.CapabilitiesAsync(CancellationToken.None);
        Assert.That(response.ResponseCode, Is.EqualTo(101));
        Assert.That(response.Capabilities, Is.EqualTo(new[]
        {
            "VERSION 2", "READER", ".STUFFED", "IHAVE"
        }));
    }

    [Test]
    public async Task ModeReaderAsync_ReturnsServerReady()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command == "QUIT")
            {
                await writer.WriteLineAsync("205 Connection closing");
                return;
            }

            Assert.That(command, Is.EqualTo("MODE READER"));
            await writer.WriteLineAsync("200 Reader mode enabled");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.ModeReaderAsync(CancellationToken.None);
        Assert.That(response.ResponseCode, Is.EqualTo(200));
    }

    [Test]
    public async Task DateAsync_UnexpectedMultiLineCode_DrainsPayloadAndKeepsSync()
    {
        var dateCalls = 0;
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command != "DATE")
            {
                return;
            }

            dateCalls++;
            if (dateCalls == 1)
            {
                await writer.WriteAsync("100 Help text follows\r\nhelp line\r\n.\r\n");
            }
            else
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var first = await client.DateAsync(CancellationToken.None);
        Assert.That(first.ResponseCode, Is.EqualTo(100));
        Assert.That(first.DateTime, Is.Null);
        var second = await client.DateAsync(CancellationToken.None);
        Assert.That(second.ResponseCode, Is.EqualTo(111));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task ModeReaderAsync_UnexpectedMultiLineCode_DrainsPayloadAndKeepsSync()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command == "MODE READER")
            {
                await writer.WriteAsync("100 Help text follows\r\nhelp line\r\n.\r\n");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var mode = await client.ModeReaderAsync(CancellationToken.None);
        Assert.That(mode.ResponseCode, Is.EqualTo(100));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo(111));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task AuthenticateAsync_UnexpectedMultiLineCode_DrainsPayloadAndKeepsSync()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("AUTHINFO USER", StringComparison.Ordinal))
            {
                await writer.WriteAsync("215 information follows\r\ninfo\r\n.\r\n");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var auth = await client.AuthenticateAsync("user", "pass", CancellationToken.None);
        Assert.That(auth.ResponseCode, Is.EqualTo(215));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo(111));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task DateAsync_UnexpectedMultiLineDrainOverflow_PoisonsConnection()
    {
        var huge = new string('x', 2048);
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("100 overflow\r\n");
            for (var i = 0; i < 8; i++)
            {
                await writer.WriteAsync(huge + "\r\n");
            }

            await writer.WriteAsync(".\r\n");
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            AbandonedBodyDrainLimit = 1024
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.DateAsync(CancellationToken.None);
        Assert.That(response.ResponseCode, Is.EqualTo(100));
        Assert.That(client.IsHealthy, Is.False);
    }

    [TestCase("400 service unavailable", true)]
    [TestCase("502 permission denied", false)]
    public async Task ConnectAsync_GreetingFailure_SetsIsTransient(string greeting, bool isTransient)
    {
        await using var server = ScriptedNntpServer.WithGreeting(greeting);
        await using var client = new UsenetClient();

        var exception = Assert.ThrowsAsync<UsenetConnectionException>(() =>
            client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None));
        Assert.That(exception!.IsTransient, Is.EqualTo(isTransient));
    }

    [Test]
    public async Task QuitAsync_ClosesConnectionAfter205()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            Assert.That(command, Is.EqualTo("QUIT"));
            await writer.WriteLineAsync("205 Connection closing");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.QuitAsync(CancellationToken.None);
        Assert.That(response.ResponseCode, Is.EqualTo((int)UsenetResponseType.ConnectionClosing));
        Assert.That(client.IsConnected, Is.False);
    }

    [Test]
    public async Task DisposeAsync_SendsBestEffortQuitWhenHealthy()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        await client.DisposeAsync();

        Assert.That(server.Commands, Does.Contain("QUIT"));
    }

    [Test]
    public async Task DecodedBodiesAsync_ExceedingMaxPipelineDepth_ThrowsBeforeWriting()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            MaxPipelineDepth = 2
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<ArgumentException>(() => client.DecodedBodiesAsync(
            new SegmentId[] { "a@example.com", "b@example.com", "c@example.com" },
            CancellationToken.None));
        Assert.That(server.Commands, Is.Empty);
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task DecodedBodiesAsync_WriteTimeout_PoisonsConnection()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromMilliseconds(200)
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var writeStream = new ControllableWriteStream();
        writeStream.SetMode(ControllableWriteStream.WriteMode.BlockUntilCancelled);
        client.ReplaceConnectionStreamForTests(writeStream);

        Assert.ThrowsAsync<TimeoutException>(() => client.DecodedBodiesAsync(
            new SegmentId[] { "article@example.com" },
            CancellationToken.None));
        Assert.That(client.IsHealthy, Is.False);
        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.StatAsync("other@example.com", CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("connection unusable"));
    }

    [Test]
    public async Task DecodedBodiesAsync_WriteCancelled_PoisonsConnection()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(30)
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var writeStream = new ControllableWriteStream();
        writeStream.SetMode(ControllableWriteStream.WriteMode.BlockUntilCancelled);
        client.ReplaceConnectionStreamForTests(writeStream);

        using var cts = new CancellationTokenSource();
        var batchTask = client.DecodedBodiesAsync(
            new SegmentId[] { "article@example.com" },
            cts.Token);
        await writeStream.WriteEntered.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await batchTask);
        Assert.That(client.IsHealthy, Is.False);
        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.StatAsync("other@example.com", CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("connection unusable"));
    }

    [Test]
    public async Task DecodedBodiesAsync_WriteIOException_PoisonsConnection()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var writeStream = new ControllableWriteStream();
        writeStream.SetMode(ControllableWriteStream.WriteMode.ThrowIOException);
        client.ReplaceConnectionStreamForTests(writeStream);

        Assert.ThrowsAsync<IOException>(() => client.DecodedBodiesAsync(
            new SegmentId[] { "article@example.com" },
            CancellationToken.None));
        Assert.That(client.IsHealthy, Is.False);
        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.StatAsync("other@example.com", CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("connection unusable"));
    }

    [Test]
    public async Task HeadAsync_BlankLineInHeaders_ConsumesTerminatorAndKeepsSync()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("HEAD", StringComparison.Ordinal))
            {
                await writer.WriteAsync(
                    "221 0 <article@example.com>\r\n" +
                    "Header-A: 1\r\n" +
                    "\r\n" +
                    "Header-B: 2\r\n" +
                    ".\r\n");
            }
            else if (command.StartsWith("STAT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("223 0 <article@example.com>");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var head = await client.HeadAsync("article@example.com", CancellationToken.None);
        Assert.That(head.ArticleHeaders!.Headers["Header-A"], Is.EqualTo("1"));
        var stat = await client.StatAsync("article@example.com", CancellationToken.None);
        Assert.That(stat.ArticleExists, Is.True);
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task HeadAsync_BlankLineThenEof_ThrowsAndPoisonsConnection()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync(
                "221 0 <article@example.com>\r\n" +
                "Header-A: 1\r\n" +
                "\r\n");
            throw new IOException("Close scripted connection.");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.HeadAsync("article@example.com", CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("header terminator"));
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task HeadAsync_BlankLineThenOversizedJunk_ThrowsAndPoisonsConnection()
    {
        var junk = new string('x', 8192);
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("221 0 <article@example.com>\r\n\r\n");
            for (var i = 0; i < 40; i++)
            {
                await writer.WriteAsync(junk + "\r\n");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.HeadAsync("article@example.com", CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("256 KiB"));
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task BodyAsync_UnexpectedMultiLineCode_DrainsPayloadAndKeepsSync()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("BODY", StringComparison.Ordinal))
            {
                await writer.WriteAsync(
                    "220 0 <article@example.com>\r\nSubject: x\r\n\r\nbody\r\n.\r\n");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var body = await client.BodyAsync("article@example.com", CancellationToken.None);
        Assert.That(body.Stream, Is.Null);
        Assert.That(body.ResponseCode, Is.EqualTo(220));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo(111));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task StatAsync_UnexpectedMultiLineCode_DrainsPayloadAndKeepsSync()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("STAT", StringComparison.Ordinal))
            {
                await writer.WriteAsync("100 Help text follows\r\nhelp line\r\n.\r\n");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var stat = await client.StatAsync("article@example.com", CancellationToken.None);
        Assert.That(stat.ResponseCode, Is.EqualTo(100));
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo(111));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task BodyAsync_UnexpectedMultiLineDrainOverflow_PoisonsConnection()
    {
        var huge = new string('x', 2048);
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("220 overflow\r\n");
            for (var i = 0; i < 8; i++)
            {
                await writer.WriteAsync(huge + "\r\n");
            }

            await writer.WriteAsync(".\r\n");
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            AbandonedBodyDrainLimit = 1024
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var body = await client.BodyAsync("article@example.com", CancellationToken.None);
        Assert.That(body.Stream, Is.Null);
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task BodyAsync_UnterminatedBodyLineAtEof_FailsWithoutEmittingPartialLine()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("222 0 <id>\r\nBODY-DATA");
            throw new IOException("Close scripted connection.");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync(
            "article@example.com", completion.SetResult, CancellationToken.None);
        using var reader = new StreamReader(response.Stream!, Encoding.Latin1);
        Assert.ThrowsAsync<UsenetProtocolException>(async () => await reader.ReadToEndAsync());
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(ArticleBodyResult.NotRetrieved));
    }

    [Test]
    public async Task StatAsync_TruncatedStatusLineAtEof_ThrowsProtocolException()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("22");
            throw new IOException("Close scripted connection.");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.StatAsync("article@example.com", CancellationToken.None));
        Assert.That(client.IsHealthy, Is.False);
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
    public void Options_DefaultCertificateRevocationCheckModeIsNoCheck()
    {
        Assert.That(
            new UsenetClientOptions().CertificateRevocationCheckMode,
            Is.EqualTo(X509RevocationMode.NoCheck));
    }

    [TestCase(X509RevocationMode.NoCheck)]
    [TestCase(X509RevocationMode.Offline)]
    [TestCase(X509RevocationMode.Online)]
    public void Constructor_AcceptsDefinedCertificateRevocationCheckMode(
        X509RevocationMode certificateRevocationCheckMode)
    {
        Assert.DoesNotThrow(() =>
        {
            using var client = new UsenetClient(new UsenetClientOptions
            {
                CertificateRevocationCheckMode = certificateRevocationCheckMode
            });
        });
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
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UsenetClient(new UsenetClientOptions
                {
                    CertificateRevocationCheckMode = (X509RevocationMode)(-1)
                }));
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
        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.DateAsync(CancellationToken.None));
        Assert.That(exception!.InnerException, Is.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task DateAsync_StoredBackgroundCancellationIsReportedAsProtocolFailure()
    {
        await using var server = new ScriptedNntpServer((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var interruption = new OperationCanceledException("Earlier operation was interrupted.");
        var recordConnectionFailure = typeof(UsenetClient).GetMethod(
            "RecordConnectionFailure",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(recordConnectionFailure, Is.Not.Null);
        recordConnectionFailure!.Invoke(client, [interruption]);

        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.DateAsync(CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(
                exception!.Message,
                Is.EqualTo("connection unusable after an earlier interrupted operation"));
            Assert.That(exception.InnerException, Is.SameAs(interruption));
            Assert.That(client.IsHealthy, Is.False);
            Assert.That(server.Commands, Is.Empty);
        });
    }

    [Test]
    public async Task StatAsync_ReadTimeout_PoisonsConnection()
    {
        await using var server = new ScriptedNntpServer(async (_, _, cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromMilliseconds(200)
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<TimeoutException>(() =>
            client.StatAsync("article@example.com", CancellationToken.None));
        Assert.That(client.IsHealthy, Is.False);
        var exception = Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.StatAsync("other@example.com", CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("connection unusable"));
    }

    [Test]
    public async Task HeadAsync_CancelledMidHeaders_PoisonsConnection()
    {
        var headersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ScriptedNntpServer.StartConnectionScript(async (reader, writer, ct) =>
        {
            var command = await reader.ReadLineAsync(ct);
            Assert.That(command, Does.StartWith("HEAD"));
            await writer.WriteAsync("221 0 <article@example.com>\r\nSubject: one\r\n");
            headersStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(30)
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var headTask = client.HeadAsync("article@example.com", cts.Token);
        await headersStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        try
        {
            await headTask;
            Assert.Fail("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
            // Expected when the caller cancels mid-header read.
        }

        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task ArticleAsync_OversizedHeaders_PoisonsConnection()
    {
        var hugeHeader = "X-Big: " + new string('a', 260 * 1024);
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("ARTICLE", StringComparison.Ordinal))
            {
                await writer.WriteAsync($"220 0 <article@example.com>\r\n{hugeHeader}\r\n\r\n.\r\n");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.ArticleAsync("article@example.com", CancellationToken.None));
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task StatAsync_CleanNoSuchArticle_KeepsConnectionHealthy()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("STAT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("430 No such article");
            }
            else if (command == "DATE")
            {
                await writer.WriteLineAsync("111 20260709213000");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var missing = await client.StatAsync("missing@example.com", CancellationToken.None);
        Assert.That(missing.ArticleExists, Is.False);
        Assert.That(client.IsHealthy, Is.True);
        var date = await client.DateAsync(CancellationToken.None);
        Assert.That(date.ResponseCode, Is.EqualTo(111));
    }

    [TestCase("+22 hello")]
    [TestCase(" 22 hello")]
    [TestCase("22 hello")]
    [TestCase("999 hello")]
    public async Task ParseResponseCode_RejectsMalformedStatusLines(string malformed)
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteLineAsync(malformed));
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        Assert.ThrowsAsync<UsenetProtocolException>(() =>
            client.StatAsync("article@example.com", CancellationToken.None));
        Assert.That(client.IsHealthy, Is.False);
    }

    [Test]
    public async Task ParseResponseCode_AcceptsBareThreeDigitCode()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            if (command.StartsWith("STAT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("430");
            }
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var response = await client.StatAsync("article@example.com", CancellationToken.None);
        Assert.That(response.ResponseCode, Is.EqualTo(430));
        Assert.That(client.IsHealthy, Is.True);
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

    private static async Task WriteYencArticleAsync(
        StreamWriter writer,
        byte[] decoded,
        string yendFields,
        bool multipart = false)
    {
        int? column = 0;
        var encoded = RapidYencSharp.YencEncoder.EncodeEx(
            decoded, ref column, 128, true);
        var wireEncoded = DotStuff(encoded);
        var multipartFields = multipart ? " part=2 total=2" : string.Empty;

        await writer.WriteAsync(
            $"222 body follows\r\n" +
            $"=ybegin line=128 size={decoded.Length}{multipartFields} name=crc.bin\r\n");
        if (multipart)
        {
            await writer.WriteAsync(
                $"=ypart begin=1 end={decoded.Length}\r\n");
        }

        await writer.WriteAsync(Encoding.Latin1.GetString(wireEncoded));
        await writer.WriteAsync($"\r\n=yend {yendFields}\r\n.\r\n");
    }

    private static async Task WriteSimpleYencArticleAsync(
        StreamWriter writer,
        byte[] decoded,
        string yendFields,
        string fileName = "pipelined.bin")
    {
        var encoded = new List<byte>(decoded.Length);
        foreach (var value in decoded)
        {
            var encodedValue = unchecked((byte)(value + 42));
            if (encodedValue is 0 or (byte)'\r' or (byte)'\n' or (byte)'=')
            {
                encoded.Add((byte)'=');
                encodedValue = unchecked((byte)(encodedValue + 64));
            }

            encoded.Add(encodedValue);
        }

        await writer.WriteAsync(
            $"222 body follows\r\n" +
            $"=ybegin line=128 size={decoded.Length} name={fileName}\r\n" +
            (encoded.Count > 0
                ? Encoding.Latin1.GetString(encoded.ToArray()) + "\r\n"
                : string.Empty) +
            $"=yend {yendFields}\r\n.\r\n");
    }
}
