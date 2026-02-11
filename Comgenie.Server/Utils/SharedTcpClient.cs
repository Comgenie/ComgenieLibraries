using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    /// <summary>
    /// This is a stand alone utility class to handle shared TCP connections. 
    /// It can be used to do multiple http(s) requests to the same host, while keeping the connection open.
    /// </summary>
    public class SharedTcpClient : IDisposable
    {
        private const bool Debug = false;
        private static List<OpenConnection> ExistingConnections = new List<OpenConnection>();
        private static object ExistingConnectionsLockObj = new object();

        private OpenConnection? Connection;
        private static int InstanceCount = 0;
        private int CurrentInstanceNumber = 0;

        public async Task ConnectAsync(string host, int port, bool ssl, int closeAfterSeconds = 60, CancellationToken cancellationToken = default)
        {
            CurrentInstanceNumber = ++InstanceCount;
            // Check if there is any open connection to reuse            

            List<OpenConnection>? expiredConnections = null;
            Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Before lock");

            lock (ExistingConnectionsLockObj)
            {
                // Remove expired connections
                Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Removing expired connections (part 1)");

                expiredConnections = ExistingConnections.Where(a => !a.InUse && a.LastActivity.AddSeconds(a.CloseAfterSeconds) < DateTime.UtcNow).ToList();
                ExistingConnections = ExistingConnections.Where(a => !expiredConnections.Contains(a)).ToList();

                // Find an existing connection
                Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Find existing connection");
                var connection = ExistingConnections.FirstOrDefault(a => !a.InUse && a.Host == host && a.Port == port && a.Ssl == ssl);
                if (connection != null)
                {
                    Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Found!");
                    connection.InUse = true;
                    Connection = connection;
                }
            }

            Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Removing expired connections (part 2)");
            foreach (var expiredConnection in expiredConnections)
            {
                try
                {
                    expiredConnection.Stream.Dispose(); // Should also close the socket
                }
                catch { }

                try
                {
                    expiredConnection.Socket.Dispose();
                }
                catch { }
            }

            if (Connection != null)
            {
                Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Returning existing connection");
                return;
            }

            // Create a new connection
            Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Create socket");
            var socket = port == 0 ? 
                new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) :
                new Socket(SocketType.Stream, ProtocolType.Tcp);

            try
            {
                Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Before connect");

                if (port == 0)
                {   // Assuming unix domain socket
                    var socketPath = host.Contains("/") || host.Contains("\\") ?
                        host : // Path was provided
                        Path.Combine(Path.GetTempPath(), host); // Default to temp path

                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                }
                else
                    await socket.ConnectAsync(host, port, cancellationToken);
                
                Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " After connect");
                Stream stream = new NetworkStream(socket, true);
                if (ssl)
                {
                    Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Before ssl stream auth");
                    stream = new SslStream(stream, false);
                    await ((SslStream)stream).AuthenticateAsClientAsync(new SslClientAuthenticationOptions() { TargetHost = host }, cancellationToken);
                    Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " After ssl stream auth");
                }

                Connection = new OpenConnection()
                {
                    Host = host,
                    Port = port,
                    Ssl = ssl,
                    CloseAfterSeconds = closeAfterSeconds,
                    InUse = true,
                    LastActivity = DateTime.UtcNow,
                    Socket = socket,
                    Stream = new RewindableStream(stream)
                };

                lock (ExistingConnectionsLockObj)
                    ExistingConnections.Add(Connection);
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(SharedTcpClient), CurrentInstanceNumber + " Could not connect to " + host+ ":"+port +", " + ex.Message);
                try
                {
                    socket.Dispose();
                } catch {}

                throw;
            }
        }

        public void Dispose()
        {
            // If the connection is still open, mark as available
            if (Connection == null)
                return;

            if (Connection.CanReuse && Connection.Socket.Connected)
            {
                Connection.LastActivity = DateTime.UtcNow;
                Connection.InUse = false;
            }
            else
            {
                // If not, remove from the existing connections
                lock (ExistingConnectionsLockObj)
                    ExistingConnections.Remove(Connection);

                try
                {
                    Connection.Stream.Dispose();
                }
                catch { }

                try
                {
                    Connection.Socket.Dispose();
                }
                catch { }
            }
        }

        private class OpenConnection
        {
            public required string Host { get; set; }
            public required int Port { get; set; }
            public required bool Ssl { get; set; }
            public required Socket Socket { get; set; }
            public required RewindableStream Stream { get; set; }
            public int CloseAfterSeconds { get; set; }
            public DateTime LastActivity { get; set; }
            public bool InUse { get; set; }
            public bool CanReuse { get; set; }
        }


        /// Helper methods

        // Execute http request and returns a stream with the full response
        public static async Task<SingleHttpResponseStream> ExecuteHttpRequest(string url, string requestHeaders, Stream? requestContent, CancellationToken cancellationToken = default)
        {
            
            var uri = new Uri(url);
            Log.Debug(nameof(SharedTcpClient), "Get shared client for " + uri.Host);
            SharedTcpClient client;
            if (uri.Scheme == "uds") // Unix Domain Sockets:  uds://(Encoded socket file path)/actual url
            {
                string socketPath = System.Net.WebUtility.UrlDecode(uri.Host); // "/tmp/my.sock"

                Log.Debug(nameof(SharedTcpClient), "Connecting to unix domain socket path: " + socketPath);
                client = new SharedTcpClient();
                await client.ConnectAsync(socketPath, 0, false, cancellationToken: cancellationToken);
            }
            else
            {
                client = new SharedTcpClient();
                await client.ConnectAsync(uri.Host, uri.Port, uri.Port == 443, cancellationToken: cancellationToken);
            }
            
            client.Connection.CanReuse = false;
            Log.Debug(nameof(SharedTcpClient), "Send headers");
            await client.Connection.Stream.WriteAsync(Encoding.ASCII.GetBytes(requestHeaders), cancellationToken);
            if (requestContent != null)
                await requestContent.CopyToAsync(client.Connection.Stream, cancellationToken);
            Log.Debug(nameof(SharedTcpClient), "Before flush");
            await client.Connection.Stream.FlushAsync(cancellationToken);
            Log.Debug(nameof(SharedTcpClient), "After flush");
            var responseStream = new SingleHttpResponseStream(client);
            await responseStream.ReadResponseHeadersAsync(cancellationToken);
            return responseStream;
        }

        public class SingleHttpResponseStream : Stream, IDisposable
        {
            private SharedTcpClient Client { get; set; }
            //public string RequestHeaders { get; set; }
            public string ResponseHeaders { get; set; }
            private long CurrentContentLength { get; set; }
            private long CurrentDataPos { get; set; }
            private bool TransferEncodingChunked { get; set; }
            public bool IncludeChunkedHeadersInResponse { get; set; }

            public SingleHttpResponseStream(SharedTcpClient sharedTcpClient)
            {
                Client = sharedTcpClient;
            }
            public async Task ReadResponseHeadersAsync(CancellationToken cancellationToken = default)
            {
                // Get response headers
                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Start initial reading buffer");

                byte[] buffer = new byte[1024 * 10];
                var headerLength = await Client.Connection.Stream.ReadTillBytesAsync(buffer, new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' }, cancellationToken: cancellationToken);
                
                if (headerLength <= 0)
                {
                    Client.Connection.Socket.Close();
                    throw new Exception("Invalid response in proxy request or connection prematurely closed");
                }

                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " End initial reading buffer");
                ResponseHeaders = Encoding.ASCII.GetString(buffer, 0, headerLength);

                if (ResponseHeaders.Contains("Transfer-Encoding: chunked"))
                {
                    TransferEncodingChunked = true; // The read call will handle the content lengths
                    CurrentContentLength = -1;
                    CurrentDataPos = 0;
                    return;
                }

                var contentLength = ResponseHeaders.IndexOf("Content-Length:");
                if (contentLength < 0)
                {
                    // No content length, we cannot reuse this tcp client
                    CurrentContentLength = -1;
                    CurrentDataPos = 0;
                    return;
                }

                var contentLengthStr = ResponseHeaders.Substring(contentLength + 15);
                var endPos = contentLengthStr.IndexOf("\r\n");
                if (endPos < 0)
                {
                    // Invalid Content-Length header
                    Client.Connection.Socket.Close();
                    throw new Exception("Invalid Content-Length header in proxy response");
                }
                contentLengthStr = contentLengthStr.Substring(0, endPos).Trim();

                long contentLengthValue;
                if (!long.TryParse(contentLengthStr, out contentLengthValue))
                {
                    Client.Connection.Socket.Close();
                    throw new Exception("Invalid Content-Length number in proxy response");
                }

                CurrentContentLength = contentLengthValue;
                CurrentDataPos = 0;

                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Initial preparing buffer");
                // Got all information, let the Read calls handle the rest
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;

            public override long Length => CurrentContentLength;

            public override long Position { get; set; }
            private bool CurrentContentEnded { get; set; } = false;

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Start reading (Content length " + CurrentContentLength + ")");

                if (CurrentContentLength == 0)
                {
                    Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " No more data, returned 0");
                    return 0;
                }

                if (CurrentContentLength < 0 && TransferEncodingChunked) // Read the first content length line (12AB\r\n) or any of the next content length lines (\r\n12AB\r\n)
                {
                    byte[] contentLengthBuffer = new byte[100];

                    var length = await Client.Connection.Stream.ReadTillBytesAsync(contentLengthBuffer, new byte[] { (byte)'\r', (byte)'\n' }, startingPos: 1, cancellationToken: cancellationToken);
                    if (length == 0)
                    {
                        Log.Warning(nameof(SingleHttpResponseStream), "Invalid content length line (1)");
                        Client.Connection.Socket.Close();
                        return 0;
                    }

                    var contentLengthStr = Encoding.ASCII.GetString(contentLengthBuffer, 0, length).Trim('\r','\n');
                    long contentLengthLong = 0;
                    try
                    {
                        contentLengthLong = Convert.ToInt64(contentLengthStr, 16);
                    }
                    catch (Exception e)
                    {
                        // Invalid content length line
                        Log.Warning(nameof(SingleHttpResponseStream), "Invalid content length line (2): " + e.Message);
                        Client.Connection.Socket.Close();
                        return 0;
                    }

                    CurrentContentLength = contentLengthLong;
                    CurrentDataPos = 0;

                    if (contentLengthLong == 0)
                    {
                        Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " This content is ended");
                        CurrentContentEnded = true;
                        // Content is might be ended, it might end with some trailing enters, but when there is a white-line, its actually ended
                        var a = Client.Connection.Stream.ReadByte();
                        var b = Client.Connection.Stream.ReadByte();
                        if (a < 0 || b < 0)
                        {
                            Log.Warning(nameof(SingleHttpResponseStream), "Invalid content length line (3)");
                            Client.Connection.Socket.Close();
                            return 0;
                        }

                        if (a != '\r' || b != '\n')
                            Log.Info(nameof(SingleHttpResponseStream), "Actually not ended...");
                        length += 2;
                        
                        if (!IncludeChunkedHeadersInResponse)
                            return 0;
                    }

                    if (IncludeChunkedHeadersInResponse)
                    {
                        Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Rewinding stream by " + length + " bytes, current content length: " + CurrentContentLength);
                        Client.Connection.Stream.Rewind(length);
                        CurrentContentLength += length;
                    }
                }

                if (CurrentContentLength > 0 && count + CurrentDataPos > CurrentContentLength)
                    count = (int)CurrentContentLength - (int)CurrentDataPos;

                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Just before actual read");
                var len = await Client.Connection.Stream.ReadAsync(buffer, offset, count, cancellationToken);

                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Just after actual read");

                if (len == 0) // Connection closed
                {
                    Client.Connection.Socket.Close();
                    if (CurrentContentLength >= 0) // We did not expect this behaviour, so we will close the socket to make sure this connection won't be reused
                        Client.Connection.Socket.Close();
                    CurrentContentLength = 0;
                    Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Connection closed, returned 0");

                    return 0;
                }


                CurrentDataPos += len;

                if (CurrentDataPos == CurrentContentLength) // End of current data reached
                {
                    if (TransferEncodingChunked && !CurrentContentEnded)
                    {
                        // Next read call will retrieve length for next chunk
                        Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Not yet finished with request, so setting current content length to -1");
                        CurrentContentEnded = false;
                        CurrentContentLength = -1;
                    }
                    else
                    {
                        Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Finished with request so setting current content length to 0, Chunked: " + TransferEncodingChunked);
                        CurrentContentLength = 0; // Set current content length to 0 to indicate we are fully finished handling this response
                    }
                }
                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " End reading, returned " + len);
                return len;
            }

            protected override void Dispose(bool disposing)
            {
                // If all data has been read, the CurrentContentLength will be 0
                // During invalid responses the socket will be closed and it won't be reused anyway
                if (CurrentContentLength == 0)
                    Client.Connection.CanReuse = true;

                Client.Dispose();

                base.Dispose(disposing);
            }

            // Unused
            public override long Seek(long offset, SeekOrigin origin)
            {
                return 0;
            }
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }
            public override void Flush() { }
        }
    }


}
