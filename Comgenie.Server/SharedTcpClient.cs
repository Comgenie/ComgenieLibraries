using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server
{
    public class SharedTcpClient : IDisposable
    {
        private const bool Debug = false;
        private static List<OpenConnection> ExistingConnections = new List<OpenConnection>();

        public OpenConnection Connection = null;
        private static int InstanceCount = 0;
        public int CurrentInstanceNumber = 0;

        public SharedTcpClient(string host, int port, bool ssl, int closeAfterSeconds=60)
        {
            CurrentInstanceNumber = ++InstanceCount;
            // Check if there is any open connection to reuse            

            List<OpenConnection> expiredConnections = null;
            Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Before lock");
            
            lock (ExistingConnections) {
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
                expiredConnection.Stream.Close(); // Also closes the socket

            if (Connection != null)
            {
                Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Returning existing connection");
                return;
            }

            // Create a new connection
            Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Create socket");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Before connect");
            socket.Connect(host, port);
            Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " After connect");
            Stream stream = new NetworkStream(socket, true);
            if (ssl)
            {
                Log.Debug(nameof(SharedTcpClient), CurrentInstanceNumber + " Before ssl stream auth");
                stream = new SslStream(stream, false);
                ((SslStream)stream).AuthenticateAsClient(host);
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
                Stream = stream
            };

            lock (ExistingConnections)
                ExistingConnections.Add(Connection);
        }        
        
        public void Dispose()
        {
            // If the connection is still open, mark as available
            if (Connection.CanReuse && Connection.Socket.Connected)
            {
                Connection.LastActivity = DateTime.UtcNow;
                Connection.InUse = false;
            }
            else
            {
                // If not, remove from the existing connections
                lock (ExistingConnections)
                    ExistingConnections.Remove(Connection);
            }            
        }

        public class OpenConnection
        {
            public string Host { get; set; }
            public int Port { get; set; }
            public bool Ssl { get; set; }
            public Socket Socket { get; set; }
            public Stream Stream { get; set; }
            public int CloseAfterSeconds { get; set; }
            public DateTime LastActivity { get; set; }
            public bool InUse { get; set; }
            public bool CanReuse { get; set; }
        }


        /// Helper methods

        // Execute http request and returns a stream with the full response
        public static SingleHttpResponseStream ExecuteHttpRequest(string url, string requestHeaders, Stream requestContent)
        {
            var uri = new Uri(url);
            Log.Debug(nameof(SharedTcpClient), "Get shared client for " + uri.Host);
            var client = new SharedTcpClient(uri.Host, uri.Port, uri.Port == 443);            
            client.Connection.CanReuse = false;
            Log.Debug(nameof(SharedTcpClient), "Send headers");
            client.Connection.Stream.Write(ASCIIEncoding.ASCII.GetBytes(requestHeaders));
            if (requestContent != null)
                requestContent.CopyTo(client.Connection.Stream);
            Log.Debug(nameof(SharedTcpClient), "Before flush");
            client.Connection.Stream.Flush();
            Log.Debug(nameof(SharedTcpClient), "After flush");
            return new SingleHttpResponseStream(client);                            
        }

        public class SingleHttpResponseStream : Stream
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
                this.Client = sharedTcpClient;

                // Get response headers
                var responseHeader = new StringBuilder();
                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Start initial reading buffer");
                while (responseHeader.Length < 1024 * 10) // Allow max 10 kb of response headers
                {
                    // TODO: Keep an internal buffer so we don't have to read bytes 1 at the time
                    var x = sharedTcpClient.Connection.Stream.ReadByte();
                    if (x == -1)
                    {
                        sharedTcpClient.Connection.Socket.Close();
                        throw new Exception("Connection prematurely closed");
                    }
                    responseHeader.Append((char)x);
                    if (x == '\n' && responseHeader.Length > 3 && responseHeader[responseHeader.Length - 2] == '\r' && responseHeader[responseHeader.Length - 3] == '\n' && responseHeader[responseHeader.Length - 4] == '\r')
                        break;
                }

                if (responseHeader.Length >= 1024 * 10)
                {
                    sharedTcpClient.Connection.Socket.Close();
                    throw new Exception("Invalid response in proxy request (too big)");
                }
                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " End initial reading buffer");
                ResponseHeaders = responseHeader.ToString();

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
                    sharedTcpClient.Connection.Socket.Close();
                    throw new Exception("Invalid Content-Length header in proxy response");
                }
                contentLengthStr = contentLengthStr.Substring(0, endPos).Trim();

                long contentLengthValue;
                if (!long.TryParse(contentLengthStr, out contentLengthValue))
                {
                    sharedTcpClient.Connection.Socket.Close();
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
            private byte[] ReadFirstBuffer = null;
            private int ReadFirstBufferIndex = 0;
            public override int Read(byte[] buffer, int offset, int count)
            {
                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Start reading");
                if (ReadFirstBuffer != null)
                {
                    int curLen;
                    if (count >= ReadFirstBuffer.Length - ReadFirstBufferIndex)
                    {
                        // Full
                        curLen = ReadFirstBuffer.Length - ReadFirstBufferIndex;
                        Buffer.BlockCopy(ReadFirstBuffer, ReadFirstBufferIndex, buffer, offset, ReadFirstBuffer.Length - ReadFirstBufferIndex);                        
                    }
                    else
                    {
                        // Partial
                        Buffer.BlockCopy(ReadFirstBuffer, ReadFirstBufferIndex, buffer, offset, count);
                        curLen = count;
                    }

                    ReadFirstBufferIndex += curLen;

                    if (ReadFirstBufferIndex == ReadFirstBuffer.Length)
                    {
                        ReadFirstBuffer = null;
                        ReadFirstBufferIndex = 0;
                    }

                    Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Return partial buffer");
                    return curLen;
                }

                if (CurrentContentLength == 0)
                    return 0;
                
                if (CurrentContentLength < 0 && TransferEncodingChunked) // Read the first content length line (12AB\r\n) or any of the next content length lines (\r\n12AB\r\n)
                {
                    // TODO: Keep an internal buffer so we don't have to read bytes 1 at the time
                    var contentLength = new StringBuilder();
                    var fullBytes = new List<byte>();
                    while (contentLength.Length < 100)
                    {
                        var x = Client.Connection.Stream.ReadByte();
                        if (x == -1)
                        {
                            Log.Warning(nameof(SingleHttpResponseStream), "Connection closed halfway during content");
                            Client.Connection.Socket.Close();
                            return 0;
                        }
                        contentLength.Append((char)x);
                        fullBytes.Add((byte)x);
                        if (x == '\n' && contentLength.Length > 1 && contentLength[contentLength.Length - 2] == '\r')
                        {
                            if (contentLength.Length == 2)
                                contentLength.Clear();
                            else
                            {
                                contentLength.Remove(contentLength.Length - 2, 2);
                                break;
                            }
                        }
                    }
                    if (contentLength.Length == 100)
                    {
                        // Invalid content length line
                        Log.Warning(nameof(SingleHttpResponseStream), "Invalid content length line (1)");
                        Client.Connection.Socket.Close();
                        return 0;
                    }
                    
                    var contentLengthStr = contentLength.ToString();
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
                        // Content is might be ended, it might end with some trailing enters, but when there is a white-line, its actually ended
                        for (var i=0;i<2;i++)
                            fullBytes.Add((byte)Client.Connection.Stream.ReadByte());

                        if (fullBytes[fullBytes.Count - 2] != '\r' && fullBytes[fullBytes.Count - 1] != '\n') // Not ended
                        {
                            Log.Info(nameof(SingleHttpResponseStream), "Actually not ended...");
                        }
                        
                        if (!IncludeChunkedHeadersInResponse)
                            return 0;
                    }

                    if (IncludeChunkedHeadersInResponse)
                    {
                        ReadFirstBuffer = fullBytes.ToArray();
                        ReadFirstBufferIndex = 0;
                        return Read(buffer, offset, count);
                    }
                }

                if (CurrentContentLength > 0 && count + CurrentDataPos > CurrentContentLength)
                    count = (int)CurrentContentLength - (int)CurrentDataPos;

                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Just before actual read");
                var len = Client.Connection.Stream.Read(buffer, offset, count);

                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " Just after actual read");

                if (len == 0) // Connection closed
                {
                    Client.Connection.Socket.Close();
                    if (CurrentContentLength >= 0) // We did not expect this behaviour, so we will close the socket to make sure this connection won't be reused
                        Client.Connection.Socket.Close();
                    CurrentContentLength = 0;
                    return 0;
                }


                CurrentDataPos += len;

                if (CurrentDataPos == CurrentContentLength) // End of current data reached
                {
                    if (TransferEncodingChunked)
                    {
                        // Next read call will retrieve length for next chunk
                        CurrentContentLength = -1;
                    }
                    else
                    {
                        CurrentContentLength = 0; // Set current content length to 0 to indicate we are fully finished handling this response
                    }
                }
                Log.Debug(nameof(SingleHttpResponseStream), Client.CurrentInstanceNumber + " End reading");
                return len;
            }

            public new void Dispose()
            {
                base.Dispose();

                // If all data has been read, the CurrentContentLength will be 0
                // During invalid responses the socket will be closed and it won't be reused anyway
                if (CurrentContentLength == 0)
                    this.Client.Connection.CanReuse = true; 

                Client.Dispose();                
            }

            // Unused
            public override long Seek(long offset, SeekOrigin origin)
            {
                return 0;
            }
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) {}
            public override void Flush() { }
        }
    }

    
}
