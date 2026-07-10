using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using UsenetSharp.Concurrency;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private NntpLineReader? _reader;
    private StreamWriter? _writer;
    private readonly AsyncSemaphore _commandLock = new(1);
    private CancellationTokenSource _connectionCts = new();
    private readonly object _connectionCtsLock = new();
    private readonly object _stateLock = new();
    private volatile ExceptionDispatchInfo? _backgroundException;
}
