using Comgenie.Server.Handlers;
using Comgenie.Server.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server
{
    public class Client
    {
        public Server? Server { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public required string RemoteAddress { get; set; }
        public required int RemoteAddressPort { get; set; }
        public required IConnectionHandler Handler { get; set; }
        public Socket? Socket { get; set; } // Socket can be null (Currently used for Remote instances), we will skip connected checks then
        public Stream? Stream { get; set; }        
        public bool StreamIsReady { get; set; }
        public bool StreamIsEncrypted { get; set; }
        public NetworkStream? NetworkStream { get; set; }
        public bool ReadOneByOne { get; set; }
        public DateTime ConnectMoment { get; set; }
        public DateTime LastDataReceivedMoment { get; set; }
        public DateTime LastDataSentMoment { get; set; }
        public object? Data { get; set; }
        private byte[]? SendBuffer { get; set; }
        private ConcurrentQueue<Tuple<byte[], int, Action?>> IncomingBuffers { get; set; } = new ConcurrentQueue<Tuple<byte[], int, Action?>>();
        private SemaphoreSlim IncomingHandlingLock { get; set; } = new SemaphoreSlim(1);
        private SemaphoreSlim SendLock { get; set; } = new SemaphoreSlim(1);

        /// <summary>
        /// Reset the cancel-timeout at the cancellation token source attached to this client.
        /// Use this to keep a connection alive. This is also done automatically after sending data to the client.
        /// </summary>
        /// <param name="customTimeout">Custom timeout, by default it's set to 5 minutes</param>
        public void ResetTimeout(TimeSpan? customTimeout = null)
        {
            CancellationTokenSource.CancelAfter(customTimeout ?? new TimeSpan(0, 5, 0));
        }
        
        internal async Task AddIncomingBufferDataAsync(byte[] data, int dataLen, Action? callBackFinished = null, CancellationToken cancellationToken = default)
        {
            LastDataReceivedMoment = DateTime.UtcNow;
            IncomingBuffers.Enqueue(new Tuple<byte[], int, Action?>(data, dataLen, callBackFinished));
            
            await IncomingHandlingLock.WaitAsync(cancellationToken);
            try
            {
                // Always grab the first incoming buffer, this makes sure the data is kept in order even if multiple work is scheduled for this client
                Tuple<byte[], int, Action?>? workBuffer;
                if (!IncomingBuffers.TryDequeue(out workBuffer))
                {
                    // Should not happen
                    Log.Warning(nameof(Client), "Incoming buffer is empty...");
                    return;
                }

                if (StreamIsReady && workBuffer.Item2 > 0)
                    await Handler.ClientReceiveDataAsync(this, workBuffer.Item1, workBuffer.Item2, cancellationToken);

                if (workBuffer.Item3 != null)
                    workBuffer.Item3(); // Callback finished
            }
            finally
            {
                IncomingHandlingLock.Release();
            }
        }
        
        internal async Task ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Server == null)
                throw new Exception("Read task cannot be started for a non-server bound client");

            Log.Debug(nameof(Client), "[ReadTask] Start handling client");
            try
            {
                while (Socket != null && Stream != null && Socket.Connected && Server.IsActive)
                {
                    if (!StreamIsReady)
                    {
                        await Task.Delay(25);
                        continue;
                    }

                    byte[]? buffer;
                    if (!Server.Buffers.TryPop(out buffer))
                        buffer = new byte[Server.MaxPacketSize];

                    Stream.ReadTimeout = -1;
                    var len = await Stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (len == 0 || Stream == null)
                    {
                        Log.Debug(nameof(Server), "[ReadTask] No more data, len: " + len);
                        break;
                    }

                    var processDataTask = AddIncomingBufferDataAsync(buffer, len, () =>
                    {
                        // Give buffer back to pool (unless the pool is too large)
                        if (Server.Buffers.Count < 100)
                            Server.Buffers.Push(buffer);
                    }, cancellationToken);

                    // If its a protocol with SSL upgrade command, we will always wait for the data before starting our next read async task
                    if (ReadOneByOne)
                    {
                        await processDataTask;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(nameof(Client), "[ReadTask] could not read client stream Exception: " + ex);
            }

            await DisconnectAsync();

            lock (Server.Clients)
                Server.Clients.Remove(this);

            Log.Debug(nameof(Client), "[ReadTask] Stop handling client");
        }

        /// <summary>
        /// Send data to the client
        /// </summary>
        /// <param name="buffer">byte array containing the data</param>
        /// <param name="pos">Position within the byte array where the data starts</param>
        /// <param name="len">Length of the data starting at the position within the byte array</param>
        /// <param name="flush">Call the underlying flush method (wait till the data is actually sent)</param>
        /// <param name="cancellationToken">Cancellation token to cancel this sending operation</param>
        /// <returns>Task</returns>
        public async Task SendDataAsync(byte[] buffer, int pos, int len, bool flush = true, CancellationToken cancellationToken = default)
        {
            await SendLock.WaitAsync(cancellationToken);
            try
            {
                if ((Socket != null && !Socket.Connected) || !StreamIsReady || Stream == null)
                    throw new Exception("Not connected");
                //Stream.WriteTimeout = 60 * 1000;                
                await Stream.WriteAsync(buffer, pos, len, cancellationToken);
                if (flush)
                    await Stream.FlushAsync(cancellationToken);
                LastDataSentMoment = DateTime.UtcNow;
                ResetTimeout();
            }
            catch (Exception e)
            {
                Log.Debug(nameof(Client), "Could not send stream: " + e.Message);
            }
            finally
            {
                SendLock.Release();
            }
        }

        /// <summary>
        /// Send string quickly to the client, default encoding is ASCII
        /// This always flushes after sending.
        /// </summary>
        /// <param name="str">String with data to send</param>
        /// <param name="encoding">Encoding to use, by default ASCII is used</param>
        /// <param name="cancellationToken">Cancellation token to cancel this sending operation</param>
        /// <returns>Task</returns>
        public async Task SendStringAsync(string str, Encoding? encoding = null, CancellationToken cancellationToken = default)
        {
            if (encoding == null)
                encoding = ASCIIEncoding.ASCII;
            var bytes = encoding.GetBytes(str);

            await SendLock.WaitAsync(cancellationToken); 
            try
            {
                if (Stream == null || (Socket != null && !Socket.Connected))
                    throw new Exception("Not connected");
                //Stream.WriteTimeout = 60 * 1000;                
                await Stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                await Stream.FlushAsync(cancellationToken);
                LastDataSentMoment = DateTime.UtcNow;
                ResetTimeout();
            }
            catch (Exception e)
            {
                Log.Debug(nameof(Client), "Could not send stream: " + e.Message);
            }
            finally
            {
                SendLock.Release();
            }
        }

        /// <summary>
        /// Read another stream and send all data to this client.
        /// </summary>
        /// <param name="streamToSend">Stream to end to the client</param>
        /// <param name="offset">Seek within the stream before sending</param>
        /// <param name="size">Max bytes to send from the stream, or -1 to read till end of stream</param>
        /// <param name="closeStream">Close the given stream after sending it to the client</param>
        /// <param name="flush">Flush after sending the stream</param>
        /// <param name="cancellationToken">Cancellation token to cancel this sending operation. After cancelling the stream will still be closed if closeStream is set to true.</param>
        /// <returns>Task</returns>
        public async Task<bool> SendStreamAsync(Stream streamToSend, long offset=0, long size=-1, bool closeStream=true, bool flush = true, CancellationToken cancellationToken = default)
        {
            var successfullySent = false;

            if (SendBuffer == null)
                SendBuffer = new byte[64 * 1024];
            await SendLock.WaitAsync(cancellationToken);
            try
            {
                if (offset > 0)
                    streamToSend.Seek(offset, SeekOrigin.Begin);
                    
                var readBytes = 0;
                if (size < 0)
                {
                    while ((readBytes = await streamToSend.ReadAsync(SendBuffer, 0, SendBuffer.Length, cancellationToken)) > 0)
                    {
                        
                        if ((Socket != null && !Socket.Connected) || !StreamIsReady || Stream == null)
                        {
                            Log.Debug(nameof(Client), "Remote socket disconnected while sending stream");
                            throw new Exception("Not connected");
                        }
                        
                        await Stream.WriteAsync(SendBuffer, 0, readBytes, cancellationToken);
                        LastDataSentMoment = DateTime.UtcNow;
                        ResetTimeout();
                    }
                    if (flush && Stream != null)
                        await Stream.FlushAsync(cancellationToken);
                }
                else
                {
                    while (size > 0 && (readBytes = await streamToSend.ReadAsync(SendBuffer, 0, (size > SendBuffer.Length ? SendBuffer.Length : (int)size), cancellationToken)) > 0)
                    {
                        size -= readBytes;
                        if ((Socket != null && !Socket.Connected) || !StreamIsReady || Stream == null)
                        {
                            Log.Debug(nameof(Client), "Remote socket disconnected while sending stream");
                            throw new Exception("Not connected");
                        }
                        await Stream.WriteAsync(SendBuffer, 0, readBytes, cancellationToken);
                        LastDataSentMoment = DateTime.UtcNow;
                        ResetTimeout();
                    }
                    if (flush && Stream != null)
                        await Stream.FlushAsync(cancellationToken);
                }

                successfullySent = true;
            }
            catch (Exception e)
            {
                Log.Debug(nameof(Client), "Could not send stream: " + e.Message);
            }
            

            try
            {
                if (closeStream)
                {
                    streamToSend.Close();
                    streamToSend.Dispose();
                }
            }
            catch { }

            SendLock.Release();

            return successfullySent;
        }

        /// <summary>
        /// Best way to disconnect the connection to this client. This will safely call all .Close, .Dispose and .Cancel's and triggers the ClientDisconnectAsync handler action.
        /// </summary>
        /// <returns>Task</returns>
        public async Task DisconnectAsync()
        {
            try
            {
                if (Stream != null)
                {
                    Stream.Close();
                    Stream.Dispose();
                    Stream = null;
                }
            }
            catch { }

            try
            {
                if (Socket != null && Socket.Connected)
                    Socket.Close();
            }
            catch { }

            try
            {
                if (!CancellationTokenSource.IsCancellationRequested)
                    CancellationTokenSource.Cancel();
            }
            catch { }

            try
            {
                if (Handler != null)
                    await Handler.ClientDisconnectAsync(this, CancellationToken.None);
            }
            catch { }
        }
    }
}
