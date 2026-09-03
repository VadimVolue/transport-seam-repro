using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TransportSeamRepro
{
    /// <summary>
    /// Opens an HTTP CONNECT tunnel through the proxy and hands back the stream on the far side.
    /// <para>
    ///   This is the piece of work that has to happen before AMQP can start, and the reason the
    ///   library needs a seam: the byte stream to the broker cannot be produced by opening a socket
    ///   to it. Nothing here is TLS-aware - the tunnel carries plain bytes, and whoever gets the
    ///   stream is responsible for the handshake.
    /// </para>
    /// </summary>
    internal static class ProxyTunnel
    {
        private const int MaximumResponseHeaderBytes = 8192;

        internal static async Task<Stream> OpenAsync(string proxyHost, int proxyPort,
            string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            var tcpClient = new TcpClient();
            try
            {
                await tcpClient.ConnectAsync(proxyHost, proxyPort, cancellationToken);

                // TcpClient.GetStream() hands the socket to the stream, so disposing the stream
                // closes the tunnel.
                NetworkStream stream = tcpClient.GetStream();

                // Invariant: the port goes into an HTTP request line, which is a wire protocol and
                // not something to render in the ambient culture's digits.
                string request = FormattableString.Invariant(
                    $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n");
                byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                await stream.WriteAsync(requestBytes, 0, requestBytes.Length, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                string statusLine = await ReadResponseStatusLineAsync(stream, cancellationToken);
                Output.Log("proxy", $"CONNECT {targetHost}:{targetPort} -> {statusLine}");

                if (!statusLine.Contains(" 200", StringComparison.Ordinal))
                {
                    throw new IOException($"the proxy refused the tunnel: {statusLine}");
                }

                return stream;
            }
            catch
            {
                tcpClient.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Reads the proxy response a byte at a time and stops at the end of the headers. Reading any
        /// further would swallow the first bytes of whatever the tunnel is about to carry.
        /// </summary>
        private static async Task<string> ReadResponseStatusLineAsync(Stream stream,
            CancellationToken cancellationToken)
        {
            var response = new List<byte>(256);
            byte[] one = new byte[1];
            int terminatorProgress = 0;

            while (terminatorProgress < 4)
            {
                int read = await stream.ReadAsync(one, 0, 1, cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "the proxy closed the connection before completing its response");
                }

                response.Add(one[0]);
                if (response.Count > MaximumResponseHeaderBytes)
                {
                    throw new IOException("the proxy response headers were unreasonably long");
                }

                byte expected = terminatorProgress is 0 or 2 ? (byte)'\r' : (byte)'\n';
                terminatorProgress = one[0] == expected ? terminatorProgress + 1 : 0;
            }

            string headers = Encoding.ASCII.GetString(response.ToArray());
            int endOfStatusLine = headers.IndexOf("\r\n", StringComparison.Ordinal);
            return endOfStatusLine < 0 ? headers.Trim() : headers.Substring(0, endOfStatusLine);
        }
    }
}
