using Comgenie.Server.Handlers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.HttpHandler;

namespace Comgenie.Server.Utils
{
    /// <summary>
    /// This util is the 'Client side' for remote instances. Initiate this to connect to a remote instance and
    /// tell that instance to reroute the http routes we have defined to us
    /// </summary>
    public class RemoteUtil : IDisposable
    {
        public const int MaxPacketSize = 1024 * 100; // Must be higher than the buffer used in Client.cs

        private Thread RemoteCommunicationsThread = null;
        private bool IsRunning = true;
        public RemoteUtil(string host, int port, string key, HttpHandler httpHandler= null, SmtpHandler smtpHandler = null, bool ssl=true)
        {
            // Open connection to remote host
            // Route all httpHandler routes via remote host (including new ones) to this instance
            // Route all (for a specific domain/email) incoming email from remote host back to this instance 
            RemoteCommunicationsThread = new Thread(new ThreadStart(() =>
            {
                while (IsRunning) // In case the connection drops, we will reconnect
                {
                    try
                    {
                        // Connect (using ssl)
                        Log.Debug(nameof(RemoteUtil), "Connect");
                        
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        socket.Connect(host, port);                        
                        Stream streamToMainInstance = new NetworkStream(socket, true);
                        if (ssl)
                            streamToMainInstance = new SslStream(streamToMainInstance, false);

                        lock (streamToMainInstance)
                        {
                            Log.Debug(nameof(RemoteUtil), "SSL Auth");

                            if (ssl)
                                ((SslStream)streamToMainInstance).AuthenticateAsClient(host);

                            // Send key
                            Log.Debug(nameof(RemoteUtil), "Sending key");
                            SendPacket(streamToMainInstance, 1, ASCIIEncoding.ASCII.GetBytes(key)); // Authenticate with key

                            // Send http routes we want to reroute
                            if (httpHandler != null)
                            {

                                foreach (var route in httpHandler.Routes)
                                {
                                    Log.Debug(nameof(RemoteUtil), "Sending route " + route.Key);
                                    SendPacket(streamToMainInstance, 2, ASCIIEncoding.ASCII.GetBytes(route.Key));
                                }
                            }

                            // Send SMTP domains/emails we want to reroute 
                            // TODO
                            Log.Debug(nameof(RemoteUtil), "Flush");
                            streamToMainInstance.Flush();

                        }

                        // Handle data
                        byte[] buffer = new byte[MaxPacketSize]; // Max packet size
                        int bufferPos = 0;
                        int len = 0;
                        byte command = 0;

                        Dictionary<Int64, Client> IncomingClients = new Dictionary<Int64, Client>();
                        while (IsRunning && bufferPos < buffer.Length && (len = streamToMainInstance.Read(buffer, bufferPos, buffer.Length - bufferPos)) > 0)
                        {
                            bufferPos += len;                            

                            while (IsRunning && bufferPos >= 5)
                            {
                                var expectedLength = BitConverter.ToUInt32(buffer, 1);
                                var fullPacketLength = (int)(expectedLength + sizeof(UInt32) + 1);

                                if (fullPacketLength > buffer.Length - 10)
                                {
                                    Log.Debug(nameof(RemoteUtil), "Incorrect expected package length " + expectedLength);                                   
                                    IsRunning = false;
                                    break;
                                }

                                if (bufferPos < fullPacketLength)
                                {
                                    Log.Debug(nameof(RemoteUtil), "Received data, but still waiting for more data");
                                    break;
                                }

                                // Handle command
                                command = buffer[0];
                                Log.Debug(nameof(RemoteUtil), "Incoming comand " + command + " with length " + fullPacketLength);

                                if (command == 2) // Incoming client connection ( Int64 Client id (Random) + Byte Handler + String Original client IP + Original client Port)
                                {

                                    var clientId = BitConverter.ToInt64(buffer, 5);
                                    if (!IncomingClients.ContainsKey(clientId))
                                    {
                                        var handler = buffer[5 + sizeof(Int64)];
                                        IConnectionHandler handlerObj = null;

                                        if (handler == 1)  // NOTE: Now only support for handlers with a direct response 
                                            handlerObj = httpHandler;
                                        else if (handler == 2)
                                            handlerObj = smtpHandler;
                                        Log.Debug(nameof(RemoteUtil), "Initializing handler with type " + handler + " for client " + clientId);
                                        if (handlerObj != null)
                                        {
                                            var newClient = new Client()
                                            {
                                                RemoteAddress = "",
                                                RemoteAddressPort = 0,
                                                StreamIsReady = true,
                                                ConnectMoment = DateTime.UtcNow,
                                                Handler = handlerObj
                                            };

                                            newClient.Stream = new ForwardToCallBackStream((curBuffer, curOffset, curCount) =>
                                            {
                                                Log.Debug(nameof(RemoteUtil), "Forward data for " + clientId + " from ForwardStream (len: " + curCount + ")");

                                                lock (streamToMainInstance)
                                                {
                                                    var curMaxPacketSzie = MaxPacketSize - 20;
                                                    for (var i = 0; i < curCount && newClient.StreamIsReady; i += curMaxPacketSzie)
                                                    {
                                                        var thisBatchCount = i + curMaxPacketSzie >= curCount ? curCount - i : curMaxPacketSzie;
                                                        Log.Debug(nameof(RemoteUtil), "Sending batch of " + thisBatchCount);

                                                        streamToMainInstance.WriteByte(3); // Data
                                                        streamToMainInstance.Write(BitConverter.GetBytes((UInt32)(thisBatchCount + sizeof(Int64))));
                                                        streamToMainInstance.Write(BitConverter.GetBytes((Int64)clientId));
                                                        streamToMainInstance.Write(curBuffer, curOffset + i, thisBatchCount);
                                                    }
                                                }

                                            });
                                            Log.Debug(nameof(RemoteUtil), "call 'ClientConnect' on handler");
                                            newClient.Handler.ClientConnect(newClient);
                                            IncomingClients.Add(clientId, newClient);
                                        }
                                    }
                                }
                                else if (command == 3) // Data for client connection ( Int64 Client id + byte[] Data )
                                {
                                    var clientId = BitConverter.ToInt64(buffer, 5);
                                    Log.Debug(nameof(RemoteUtil), "Got data from " + clientId);
                                    if (IncomingClients.ContainsKey(clientId))
                                    {
                                        var client = IncomingClients[clientId];

                                        byte[] actualData = new byte[expectedLength - sizeof(Int64)];
                                        Buffer.BlockCopy(buffer, 5 + sizeof(Int64), actualData, 0, actualData.Length);

                                        Log.Debug(nameof(RemoteUtil), "call 'ClientReceiveData' with  " + actualData.Length + " data");

                                        client.AddIncomingBufferData(actualData, actualData.Length, () =>
                                        {
                                            if (actualData.Length == 0) // The other side can send an empty data packet to mark the end of his data, that means by this point, we processed all data and we can send our 'end of the data'
                                            {
                                                streamToMainInstance.Flush();

                                                // We only have handlers with direct responses, so send a signal the data is finished (for now)
                                                Log.Debug(nameof(RemoteUtil), "Finished handling receive data for client " + clientId + ", send end of data packet and flush");
                                            
                                                SendPacket(streamToMainInstance, 3, BitConverter.GetBytes((Int64)clientId)); // Just send the client id and no data
                                            }

                                        });                                        

                                        
                                    }
                                }
                                else if (command == 4) // Closing client connection ( Int64 Client id ), note that this can happen halfway during execution of a request
                                {
                                    var clientId = BitConverter.ToInt64(buffer, 5);
                                    if (IncomingClients.ContainsKey(clientId))
                                    {
                                        Log.Debug(nameof(RemoteUtil), "Disconnecting client " + clientId);
                                        var client = IncomingClients[clientId];
                                        client.StreamIsReady = false;
                                        client.Handler.ClientDisconnect(client);
                                        IncomingClients.Remove(clientId);
                                    }
                                }
                                else if (command == 255)
                                {
                                    Log.Error(nameof(RemoteUtil), "Error from remote instance: " + ASCIIEncoding.ASCII.GetString(buffer, 5, (int)expectedLength));
                                    IsRunning = false;
                                    break;
                                }

                                // Move rest of buffer to start
                                if (bufferPos > fullPacketLength)
                                {
                                    Buffer.BlockCopy(buffer, fullPacketLength, buffer, 0, bufferPos - fullPacketLength);
                                    bufferPos -= fullPacketLength;
                                }
                                else
                                {
                                    bufferPos = 0;
                                }
                            }
                        }


                        if (socket.Connected)
                            socket.Close();
                    }
                    catch (Exception e)
                    {
                        Log.Error(nameof(RemoteUtil), "Exception: " + e.Message);
                    }
                }

            }));
            RemoteCommunicationsThread.Start();

        }
        private static void SendPacket(Stream streamToMainInstance, byte command, byte[] data)
        {
            lock (streamToMainInstance)
            {
                streamToMainInstance.WriteByte(command);
                streamToMainInstance.Write(BitConverter.GetBytes((UInt32)data.Length));
                streamToMainInstance.Write(data);
            }
        }

        public void Dispose()
        {
            IsRunning = false;
            RemoteCommunicationsThread.Interrupt();
            RemoteCommunicationsThread.Join();
        }

        class ForwardToCallBackStream : Stream // This is a fake stream which will just forward data to a callback
        {
            public Action<byte[], int, int> CallBack { get; set; }
            public ForwardToCallBackStream(Action<byte[], int, int> callBack)
            {
                CallBack = callBack;
            }
            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => true;

            public override long Length => 0;

            public override long Position { get; set; }

            public override void Flush()
            {
                
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return -1;
            }

            public override void SetLength(long value)
            {
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                CallBack(buffer, offset, count);
            }
        }
    }
}
