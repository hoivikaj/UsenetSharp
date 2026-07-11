using System.Net;
using System.Net.Sockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Benchmarks;

[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private static readonly SegmentId SegmentId = new("benchmark-article@example.invalid");
    private BenchmarkNntpServer? _server;
    private UsenetClient? _client;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _server = new BenchmarkNntpServer(BenchmarkPayload.YencArticle);
        _client = new UsenetClient();
        await _client.ConnectAsync("127.0.0.1", _server.Port, useSsl: false, CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }

        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    [Benchmark]
    public async Task BodyToPipeAsync()
    {
        var response = await _client!.BodyAsync(SegmentId, CancellationToken.None);
        await using var stream = response.Stream!;
        await stream.CopyToAsync(Stream.Null);

        await _client.WaitForReadyAsync(CancellationToken.None);
    }

    [Benchmark]
    public async Task BodyAndYencStreamAsync()
    {
        var response = await _client!.BodyAsync(SegmentId, CancellationToken.None);
        await using (var yenc = new YencStream(response.Stream!))
        {
            await yenc.CopyToAsync(Stream.Null);
        }

        await _client.WaitForReadyAsync(CancellationToken.None);
    }

    [Benchmark]
    public async Task DecodedBodyAsync()
    {
        var response = await _client!.DecodedBodyAsync(SegmentId, CancellationToken.None);
        await using var stream = response.Stream!;
        await stream.CopyToAsync(Stream.Null);

        await _client.WaitForReadyAsync(CancellationToken.None);
    }

    private sealed class BenchmarkNntpServer : IAsyncDisposable
    {
        private readonly string _article;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public BenchmarkNntpServer(byte[] article)
        {
            _article = Encoding.Latin1.GetString(article);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(client);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
            {
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true))
            await using (var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            })
            {
                await writer.WriteLineAsync("200 benchmark server ready");
                while (!_cts.IsCancellationRequested)
                {
                    var command = await reader.ReadLineAsync(_cts.Token);
                    if (command == null)
                    {
                        return;
                    }

                    if (!command.StartsWith("BODY ", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("500 unsupported command");
                        continue;
                    }

                    await writer.WriteLineAsync("222 0 <benchmark-article@example.invalid> body follows");
                    await writer.WriteAsync(_article);
                    await writer.WriteLineAsync(".");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
    }
}
