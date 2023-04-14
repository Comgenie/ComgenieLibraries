using Comgenie.Server.Handlers;
using Comgenie.Server.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Comgenie.Server
{
    public class Client
    {
        public Server Server { get; set; }
        public Socket Socket { get; set; } // Socket can be null (Currently used for Remote instances), we will skip connected checks then
        public Stream Stream { get; set; }        
        public CancellationTokenSource StreamReadingCancellationTokenSource { get; set; }
        public Action CancellationCallBack = null;
        public bool StreamIsReady { get; set; }
        public NetworkStream NetworkStream { get; set; }
        public IConnectionHandler Handler { get; set; }
        public int MaxProcessingCount { get; set; }
        public string RemoteAddress { get; set; }
        public int RemoteAddressPort { get; set; }
        public DateTime ConnectMoment { get; set; }
        public DateTime LastDataReceivedMoment { get; set; }
        public DateTime LastDataSentMoment { get; set; }
        public ulong TotalBytesReceived { get; set; }
        public ulong TotalBytesSent { get; set; }
        public object Data { get; set; }
        private byte[] SendBuffer { get; set; }
        private ConcurrentQueue<Tuple<byte[], int, Action>> IncomingBuffers { get; set; }
        public Client()
        {
            IncomingBuffers = new ConcurrentQueue<Tuple<byte[], int, Action>>();
        }
        public int AddIncomingBufferData(byte[] data, int dataLen, Action callBackFinished = null)
        {
            LastDataReceivedMoment = DateTime.UtcNow;
            IncomingBuffers.Enqueue(new Tuple<byte[], int, Action>(data, dataLen, callBackFinished));
            var count = IncomingBuffers.Count;

            WorkUtil.Do(() =>
            {
                lock (IncomingBuffers)
                {
                    // Always grab the first incoming buffer, this makes sure the data is kept in order even if multiple work is scheduled for this client
                    Tuple<byte[], int, Action> workBuffer;
                    if (!IncomingBuffers.TryDequeue(out workBuffer))
                    {
                        // Should not happen
                        Log.Warning(nameof(Client), "Incoming buffer is empty...");
                        return;
                    }

                    if (StreamIsReady && workBuffer.Item2 > 0)
                        Handler.ClientReceiveData(this, workBuffer.Item1, workBuffer.Item2);

                    if (workBuffer.Item3 != null)
                        workBuffer.Item3(); // Callback finished
                }
                
            });

            return count;
        }

        /// Send data quickly to the client
        public void SendData(byte[] buffer, int pos, int len, bool flush = true)
        {
            lock (this)
            {
                try
                {
                    if ((Socket != null && !Socket.Connected) || !StreamIsReady)
                        throw new Exception("Not connected");
                    //Stream.WriteTimeout = 60 * 1000;                
                    Stream.Write(buffer, pos, len);
                    if (flush)
                        Stream.Flush();
                    LastDataSentMoment = DateTime.UtcNow;
                }
                catch (Exception e)
                {
                    Log.Debug(nameof(Client), "Could not send stream: " + e.Message);
                }
            }
        }

        /// Send string quickly to the client, default encoding is ASCII
        public void SendString(string str, Encoding encoding = null)
        {
            if (encoding == null)
                encoding = ASCIIEncoding.ASCII;
            var bytes = encoding.GetBytes(str);

            lock (this)
            {
                try
                {
                    if (Stream == null || (Socket != null && !Socket.Connected))
                        throw new Exception("Not connected");
                    //Stream.WriteTimeout = 60 * 1000;                
                    Stream.Write(bytes, 0, bytes.Length);
                    Stream.Flush();
                    LastDataSentMoment = DateTime.UtcNow;
                }
                catch (Exception e)
                {
                    Log.Debug(nameof(Client), "Could not send stream: " + e.Message);
                }
            }
        }


        /// Send contents of the stream to the client
        public bool SendStream(Stream streamToSend, long offset=0, long size=-1, bool closeStream=true, bool flush = true)
        {
            var successfullySent = false;

            lock (this)
            {
                if (SendBuffer == null)
                    SendBuffer = new byte[64 * 1024];
                
                try
                {
                    if (offset > 0)
                        streamToSend.Seek(offset, SeekOrigin.Begin);
                    
                    var readBytes = 0;
                    if (size < 0)
                    {
                        while ((readBytes = streamToSend.Read(SendBuffer, 0, SendBuffer.Length)) > 0)
                        {

                            if ((Socket != null && !Socket.Connected) || !StreamIsReady)
                            {
                                Log.Debug(nameof(Client), "Remote socket disconnected while sending stream");
                                throw new Exception("Not connected");
                            }
                            Stream.Write(SendBuffer, 0, readBytes);
                            LastDataSentMoment = DateTime.UtcNow;
                        }
                        if (flush)
                            Stream.Flush();
                    }
                    else
                    {
                        while (size > 0 && (readBytes = streamToSend.Read(SendBuffer, 0, (size > SendBuffer.Length ? SendBuffer.Length : (int)size))) > 0)
                        {
                            size -= readBytes;
                            if ((Socket != null && !Socket.Connected) || !StreamIsReady)
                            {
                                Log.Debug(nameof(Client), "Remote socket disconnected while sending stream");
                                throw new Exception("Not connected");
                            }
                            Stream.Write(SendBuffer, 0, readBytes);
                            LastDataSentMoment = DateTime.UtcNow;
                        }
                        if (flush)
                            Stream.Flush();
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
            }
            return successfullySent;
        }

        public void Disconnect()
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
                    Handler.ClientDisconnect(this);
            }
            catch { }
        }
    }
}
