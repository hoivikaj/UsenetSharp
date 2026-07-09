using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UsenetSharpTest.Support;

internal sealed class ScriptedNntpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, StreamWriter, CancellationToken, Task> _commandHandler;
    private readonly Task _acceptLoop;

    public ScriptedNntpServer(Func<string, StreamWriter, CancellationToken, Task> commandHandler)
    {
        _commandHandler = commandHandler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }
    public ConcurrentQueue<string> Commands { get; } = new();

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
        { AutoFlush = true, NewLine = "\r\n" })
        {
            await writer.WriteLineAsync("200 scripted server ready");
            while (!_cts.IsCancellationRequested)
            {
                var command = await reader.ReadLineAsync(_cts.Token);
                if (command == null)
                {
                    return;
                }

                Commands.Enqueue(command);
                await _commandHandler(command, writer, _cts.Token);
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
