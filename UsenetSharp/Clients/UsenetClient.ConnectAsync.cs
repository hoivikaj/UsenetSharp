using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCommandValue(host, nameof(host), 253);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CleanupConnection();
            ThrowIfDisposed();
            using var operationCts = CreateOperationTokenSource(cancellationToken);

            _tcpClient = new TcpClient
            {
                NoDelay = true
            };
            _tcpClient.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            await _tcpClient.ConnectAsync(host, port, operationCts.Token).ConfigureAwait(false);
            _stream = _tcpClient.GetStream();

            if (useSsl)
            {
                var sslStream = new SslStream(_stream, false);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = _options.CertificateRevocationCheckMode
                }, operationCts.Token).ConfigureAwait(false);
                _stream = sslStream;
            }

            // Use Latin1 encoding to preserve exact byte values 0-255 for yEnc-encoded content
            _reader = new NntpLineReader(_stream);
            _writer = new StreamWriter(_stream, Encoding.Latin1)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            // Read the server response
            var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
            var responseCode = ParseResponseCode(response);

            // NNTP servers typically respond with "200" or "201" for successful connection
            if (responseCode != (int)UsenetResponseType.ServerReadyPostingAllowed &&
                responseCode != (int)UsenetResponseType.ServerReadyNoPostingAllowed)
                throw new UsenetConnectionException(response!) { ResponseCode = responseCode };

            Volatile.Write(ref _connectionState, 1);
        }
        catch
        {
            CleanupConnection();
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
