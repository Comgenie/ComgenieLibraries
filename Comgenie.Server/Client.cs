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
        public ulong TotalBytesReceived { get; set; }
        public ulong TotalBytesSent { get; set; }
        public object? Data { get; set; }
        private byte[]? SendBuffer { get; set; }
        private ConcurrentQueue<Tuple<byte[], int, Action?>> IncomingBuffers { get; set; } = new ConcurrentQueue<Tuple<byte[], int, Action?>>();
        private SemaphoreSlim IncomingHandlingLock { get; set; } = new SemaphoreSlim(1);
        private SemaphoreSlim SendLock { get; set; } = new SemaphoreSlim(1);
        
        internal async Task AddIncomingBufferData(byte[] data, int dataLen, Action? callBackFinished = null)
        {
            LastDataReceivedMoment = DateTime.UtcNow;
            IncomingBuffers.Enqueue(new Tuple<byte[], int, Action?>(data, dataLen, callBackFinished));
            
            await IncomingHandlingLock.WaitAsync();
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
                    await Handler.ClientReceiveData(this, workBuffer.Item1, workBuffer.Item2);

                if (workBuffer.Item3 != null)
                    workBuffer.Item3(); // Callback finished
            }
            finally
            {
                IncomingHandlingLock.Release();
            }
        }
        
        internal async Task Read()
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
                    var len = await Stream.ReadAsync(buffer, 0, buffer.Length);
                    if (len == 0 || Stream == null)
                    {
                        Log.Debug(nameof(Server), "[ReadTask] No more data, len: " + len);
                        break;
                    }

                    var processDataTask = AddIncomingBufferData(buffer, len, () =>
                    {
                        // Give buffer back to pool (unless the pool is too large)
                        if (Server.Buffers.Count < 100)
                            Server.Buffers.Push(buffer);
                    });

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

            await Disconnect();

            lock (Server.Clients)
                Server.Clients.Remove(this);

            Log.Debug(nameof(Client), "[ReadTask] Stop handling client");
        }

        /// Send data quickly to the client
        public async Task SendData(byte[] buffer, int pos, int len, bool flush = true)
        {
            await SendLock.WaitAsync();
            try
            {
                if ((Socket != null && !Socket.Connected) || !StreamIsReady || Stream == null)
                    throw new Exception("Not connected");
                //Stream.WriteTimeout = 60 * 1000;                
                await Stream.WriteAsync(buffer, pos, len);
                if (flush)
                    await Stream.FlushAsync();
                LastDataSentMoment = DateTime.UtcNow;
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

        /// Send string quickly to the client, default encoding is ASCII
        public async Task SendString(string str, Encoding? encoding = null)
        {
            if (encoding == null)
                encoding = ASCIIEncoding.ASCII;
            var bytes = encoding.GetBytes(str);

            await SendLock.WaitAsync(); 
            try
            {
                if (Stream == null || (Socket != null && !Socket.Connected))
                    throw new Exception("Not connected");
                //Stream.WriteTimeout = 60 * 1000;                
                await Stream.WriteAsync(bytes, 0, bytes.Length);
                await Stream.FlushAsync();
                LastDataSentMoment = DateTime.UtcNow;
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


        /// Send contents of the stream to the client
        public async Task<bool> SendStream(Stream streamToSend, long offset=0, long size=-1, bool closeStream=true, bool flush = true)
        {
            var successfullySent = false;

            if (SendBuffer == null)
                SendBuffer = new byte[64 * 1024];
            await SendLock.WaitAsync();
            try
            {
                if (offset > 0)
                    streamToSend.Seek(offset, SeekOrigin.Begin);
                    
                var readBytes = 0;
                if (size < 0)
                {
                    while ((readBytes = await streamToSend.ReadAsync(SendBuffer, 0, SendBuffer.Length)) > 0)
                    {

                        if ((Socket != null && !Socket.Connected) || !StreamIsReady || Stream == null)
                        {
                            Log.Debug(nameof(Client), "Remote socket disconnected while sending stream");
                            throw new Exception("Not connected");
                        }
                        
                        await Stream.WriteAsync(SendBuffer, 0, readBytes);
                        LastDataSentMoment = DateTime.UtcNow;
                    }
                    if (flush && Stream != null)
                        await Stream.FlushAsync();
                }
                else
                {
                    while (size > 0 && (readBytes = await streamToSend.ReadAsync(SendBuffer, 0, (size > SendBuffer.Length ? SendBuffer.Length : (int)size))) > 0)
                    {
                        size -= readBytes;
                        if ((Socket != null && !Socket.Connected) || !StreamIsReady || Stream == null)
                        {
                            Log.Debug(nameof(Client), "Remote socket disconnected while sending stream");
                            throw new Exception("Not connected");
                        }
                        await Stream.WriteAsync(SendBuffer, 0, readBytes);
                        LastDataSentMoment = DateTime.UtcNow;
                    }
                    if (flush && Stream != null)
                        await Stream.FlushAsync();
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

        public async Task Disconnect()
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
                if (Handler != null)
                    await Handler.ClientDisconnect(this);
            }
            catch { }
        }
    }
}
