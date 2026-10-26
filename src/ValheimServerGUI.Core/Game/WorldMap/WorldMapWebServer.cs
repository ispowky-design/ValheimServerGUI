using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>What the map's web page serves: the page itself and the map picture, and a stamp that changes with them.</summary>
    public sealed record WorldMapWebContent(string Html, byte[] Png, string Version);

    /// <summary>
    /// A tiny read-only web server that shows the world map to players who are not in game: <c>/</c> is the page,
    /// <c>/map.png</c> the picture and <c>/version</c> a stamp the page polls to reload itself when the map changes.
    /// Nothing else is served, and nothing a visitor sends is ever stored or acted on.
    ///
    /// <para>One per app (<see cref="Shared"/>), since a port can only be listened on once. Built on a plain socket
    /// rather than <c>HttpListener</c>, which on Windows needs administrator rights to accept connections from other
    /// computers.</para>
    /// </summary>
    public sealed class WorldMapWebServer : IDisposable
    {
        public static WorldMapWebServer Shared { get; } = new();

        private const int MaxRequestBytes = 8 * 1024;
        private const int MaxConnections = 32;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly object _lock = new();
        private readonly SemaphoreSlim _connections = new(MaxConnections);
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private volatile WorldMapWebContent? _content;

        /// <summary>The port being listened on, or null when stopped.</summary>
        public int? Port { get; private set; }

        public bool IsRunning => Port is not null;

        /// <summary>
        /// Starts listening on <paramref name="port"/> on every network interface, or moves to it when already running
        /// on another one. Throws <see cref="SocketException"/> when the port cannot be used (for example, another
        /// program has it).
        /// </summary>
        public void Start(int port)
        {
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            lock (_lock)
            {
                if (Port == port) return;
                StopLocked();

                var listener = Listen(port);

                _listener = listener;
                _cts = new CancellationTokenSource();
                Port = port;
                _ = AcceptLoopAsync(listener, _cts.Token);
            }
        }

        /// <summary>Listens on IPv6 and IPv4 together where the system allows it, else on IPv4 alone.</summary>
        private static TcpListener Listen(int port)
        {
            TcpListener? dual = null;
            try
            {
                dual = new TcpListener(IPAddress.IPv6Any, port);
                dual.Server.DualMode = true;
                dual.Start();
                return dual;
            }
            catch (Exception e) when (e is NotSupportedException ||
                                      e is SocketException { SocketErrorCode: not (SocketError.AddressAlreadyInUse or SocketError.AccessDenied) })
            {
                dual?.Stop();
            }

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return listener;
        }

        public void Stop()
        {
            lock (_lock) StopLocked();
        }

        /// <summary>Replaces what the page shows; visitors' pages pick it up on their next poll.</summary>
        public void Publish(WorldMapWebContent content) => _content = content;

        public void Dispose() => Stop();

        private void StopLocked()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _listener?.Stop();
            _listener = null;
            Port = null;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                if (!_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleAsync(client, cancellationToken);
                    }
                    finally
                    {
                        _connections.Release();
                    }
                }, CancellationToken.None);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken stopping)
        {
            using var _ = client;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestLineAsync(stream, timeout.Token);
                var response = Respond(request);
                await stream.WriteAsync(response, timeout.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The visitor went away or was too slow; nothing to clean up.
            }
        }

        private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[MaxRequestBytes];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0) break;
                length += read;
                if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            return end < 0 ? null : text[..end];
        }

        /// <summary>The full HTTP response to a request line (<c>GET /path HTTP/1.1</c>).</summary>
        public byte[] Respond(string? requestLine)
        {
            var parts = requestLine?.Split(' ');
            if (parts is not { Length: 3 } || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
            {
                return Response(400, "Bad Request", "text/plain; charset=utf-8", "Bad Request"u8.ToArray(), head: false);
            }

            var head = parts[0] == "HEAD";
            if (!head && parts[0] != "GET")
            {
                return Response(405, "Method Not Allowed", "text/plain; charset=utf-8", "Method Not Allowed"u8.ToArray(), head: false);
            }

            var path = parts[1];
            var query = path.IndexOf('?');
            if (query >= 0) path = path[..query];

            var content = _content;
            return path switch
            {
                "/" or "/index.html" => Response(200, "OK", "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(content?.Html ?? WorldMapWebPage.NotReady()), head),
                "/map.png" when content is not null => Response(200, "OK", "image/png", content.Png, head),
                "/version" => Response(200, "OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(content?.Version ?? ""), head),
                _ => Response(404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray(), head),
            };
        }

        private static byte[] Response(int status, string reason, string contentType, byte[] body, bool head)
        {
            var headers = $"HTTP/1.1 {status} {reason}\r\n" +
                          $"Content-Type: {contentType}\r\n" +
                          $"Content-Length: {body.Length}\r\n" +
                          "Cache-Control: no-cache\r\n" +
                          "X-Content-Type-Options: nosniff\r\n" +
                          "Referrer-Policy: no-referrer\r\n" +
                          "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(headers);
            if (head) return headerBytes;

            var response = new byte[headerBytes.Length + body.Length];
            headerBytes.CopyTo(response, 0);
            body.CopyTo(response, headerBytes.Length);
            return response;
        }
    }
}
