using System.Net;
using System.Net.Sockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace UsenetSharp.Benchmarks;

[MemoryDiagnoser]
public class StatBenchmarks
{
    private static readonly SegmentId SegmentId = new("benchmark-article@example.invalid");
    private BenchmarkStatServer? _server;
    private UsenetClient? _client;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _server = new BenchmarkStatServer();
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
    public async Task StatAsync()
    {
        await _client!.StatAsync(SegmentId, CancellationToken.None);
    }

    private sealed class BenchmarkStatServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public BenchmarkStatServer()
        {
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

                    if (!command.StartsWith("STAT ", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("500 unsupported command");
                        continue;
                    }

                    await writer.WriteLineAsync("223 0 <benchmark-article@example.invalid>");
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
