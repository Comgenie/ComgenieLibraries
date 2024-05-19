using Comgenie.Server.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.HttpHandler;

namespace Comgenie.Server.Handlers
{
    public class RemoteHandler : IConnectionHandler
    {
        // This allows another instance to connect to this instance and request and
        // - HTTP: Route requests to the remote instance
        // - SMTP: Route incoming email handling (TODO)
        private HttpHandler? _httpHandler = null;
        private SmtpHandler? _smtpHandler = null;

        private Dictionary<string, string[]?> RemoteProxyKeys = new Dictionary<string, string[]?>(); // key, specificdomains[]

        public RemoteHandler(HttpHandler? httpHandler=null, SmtpHandler? smtpHandler=null)
        {
            _httpHandler = httpHandler;
            _smtpHandler = smtpHandler;
        }

        public void AddRemoteProxyKey(string key, string[]? specificDomains = null)
        {
            RemoteProxyKeys.Add(key, specificDomains);
        }
        public void RemoveRemoteProxyKey(string key)
        {
            if (RemoteProxyKeys.ContainsKey(key))
                RemoteProxyKeys.Remove(key);
        }

        // Remote client handling:
        public Task ClientConnect(Client client)
        {
            Log.Info(nameof(RemoteHandler), "Remote client connected");

            client.Data = new RemoteClientData()
            {
                IncomingBuffer = new byte[RemoteUtil.MaxPacketSize * 2], // Our buffer is larger as we can have multiple packets
            };
            return Task.CompletedTask;
        }

        public Task ClientDisconnect(Client client)
        {
            var data = (RemoteClientData?)client.Data;

            Log.Info(nameof(RemoteHandler), "Remote client disconnected");

            // Remove all registered http and smtp routes for this client
            if (data != null)
            {
                foreach (var route in data.Routes)
                {
                    if (_httpHandler != null)
                    {
                        Log.Info(nameof(RemoteHandler), "Removing route: " + route.Item1 + "/" + route.Item2);
                        _httpHandler.RemoveRoute(route.Item1, route.Item2);
                    }
                }
            }
            // TODO: remove smtp redirects

            client.Data = null;
            return Task.CompletedTask;
        }
        public static Dictionary<Int64, BlockingCollection<byte[]>> DataQueues = new Dictionary<Int64, BlockingCollection<byte[]>>();
        public async Task ClientReceiveData(Client client, byte[] buffer, int len) // Packet: Command byte, length uint32, data
        {
            var data = (RemoteClientData?)client.Data;
            if (data == null)
                return;

            var random = new Random();
            Log.Debug(nameof(RemoteHandler), "Receiving data " + len);

            if (client.Data == null || data.IncomingBufferLength + len > data.IncomingBuffer.Length)
            {
                Log.Info(nameof(RemoteHandler), "Incorrect packet. Clearing buffer");
                data.IncomingBufferLength = 0;
                return;
            }
            

            Buffer.BlockCopy(buffer, 0, data.IncomingBuffer, data.IncomingBufferLength, len);
            data.IncomingBufferLength += len;            

            while (data.IncomingBufferLength >= 5)
            {
                byte command = data.IncomingBuffer[0];
                var expectedLength = BitConverter.ToUInt32(data.IncomingBuffer, 1);
                if (data.IncomingBufferLength - 5 < expectedLength)
                    break; // Not enough data

                Log.Debug(nameof(RemoteHandler), "Received command " + command + " with data " + expectedLength);

                if (command == 1) // Verify key
                {
                    var key = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, 5, (int)expectedLength);
                    if (RemoteProxyKeys.ContainsKey(key))
                    {
                        Log.Info(nameof(RemoteHandler), "Authenticated remote instance");
                        data.Authenticated = true;
                        data.SpecificDomains = RemoteProxyKeys[key];
                    }
                    else
                    {
                        // Send error
                        Log.Warning(nameof(RemoteHandler), "Invalid key remote instance");
                        await SendPacket(client, 255, ASCIIEncoding.ASCII.GetBytes("Wrong remote instance key"));
                    }
                }
                else if (data.Authenticated)
                {

                    if (command == 2 && _httpHandler != null) // Register http proxy
                    {
                        var routeKey = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, 5, (int)expectedLength);
                        Log.Info(nameof(RemoteHandler), "Registering route: " + routeKey);

                        if (routeKey.Contains("/"))
                        {                            
                            var domain = routeKey.Substring(0, routeKey.IndexOf("/")).ToLower();
                            if (data.SpecificDomains == null || data.SpecificDomains.Contains(domain))
                            {
                                if (client.Server != null && !client.Server.Domains.Contains(domain))
                                    client.Server.AddDomain(domain);
                                var path = routeKey.Substring(routeKey.IndexOf("/"));
                                data.Routes.Add(new Tuple<string, string>(domain, path));
                                _httpHandler.AddCustomRoute(domain, path, async (httpClient, httpClientData) =>
                                {
                                    Log.Debug(nameof(RemoteHandler), "Got request I need to reroute");                                    

                                    if (client.Data == null)
                                        throw new Exception("Connection to remote instance lost");

                                    // Build http header request
                                    StringBuilder request = new StringBuilder();
                                    request.AppendLine(httpClientData.Method + " " + httpClientData.RequestRaw + " HTTP/1.1");
                                    foreach (var requestHeader in httpClientData.Headers)
                                        request.AppendLine(requestHeader.Key + ": " + requestHeader.Value);
                                    request.AppendLine();
                                    var headerData = ASCIIEncoding.ASCII.GetBytes(request.ToString());
                                    if (headerData.Length > RemoteUtil.MaxPacketSize)
                                        throw new Exception("HTTP header too large");

                                    // Generate a random ID and prepare the data queue                                    
                                    var randomClientId = random.NextInt64();
                                    Log.Debug(nameof(RemoteHandler), "Generated new client Id " + randomClientId);
                                    
                                    var col = new BlockingCollection<byte[]>();
                                    lock (DataQueues)
                                        DataQueues.Add(randomClientId, col);

                                    try
                                    {
                                        // Send connect command ( Int64 Client id (Random) + Byte Handler + String Original client IP : Original client Port)
                                        var clientIp = ASCIIEncoding.ASCII.GetBytes(httpClient.RemoteAddress + ":" + httpClient.RemoteAddressPort);
                                        byte[] connectData = new byte[1 + clientIp.Length];
                                        connectData[0] = 1; // HTTP Handler
                                        clientIp.CopyTo(connectData, 1);
                                        await SendPacket(client, 2, connectData, clientId: randomClientId);

                                        // Send header
                                        await SendPacket(client, 3, headerData, clientId: randomClientId);

                                        // Send data
                                        if (httpClientData.DataStream != null)
                                        {
                                            var tmpPacket = new byte[RemoteUtil.MaxPacketSize];
                                            httpClientData.DataStream.Position = 0;
                                            for (var i = 0; i < httpClientData.DataStream.Length; i += RemoteUtil.MaxPacketSize)
                                            {
                                                var remaining = i + RemoteUtil.MaxPacketSize >= httpClientData.DataStream.Length ? httpClientData.DataStream.Length - i : RemoteUtil.MaxPacketSize;
                                                var tmpPacketLen = 0;
                                                while (tmpPacketLen < remaining)
                                                {
                                                    var tmpTmpPacketLen = httpClientData.DataStream.Read(tmpPacket, 0, (int)remaining);
                                                    if (tmpTmpPacketLen <= 0)
                                                    {
                                                        Log.Warning(nameof(RemoteHandler), "Incorrect posted data");
                                                        break;
                                                    }
                                                    tmpPacketLen += tmpTmpPacketLen;
                                                }                                                
                                                
                                                await SendPacket(client, 3, tmpPacket, 0, tmpPacketLen, clientId: randomClientId);
                                            }
                                        }

                                        await SendPacket(client, 3, new byte[] { }, clientId: randomClientId); // Send empty data packet to mark end of our data

                                        client.Stream.Flush();

                                        // Wait for response
                                        Log.Debug(nameof(RemoteHandler), "Waiting for response");
                                        
                                        foreach (var response in col.GetConsumingEnumerable())
                                        {
                                            Log.Debug(nameof(RemoteHandler), "Got a response with length " + response.Length);
                                            
                                            if (response.Length == 0) // This is the secret signal to shut up
                                                break;

                                            if ((httpClient.Socket != null && !httpClient.Socket.Connected) || !httpClient.StreamIsReady)
                                            {
                                                // Our client disconnected, so we will stop executing and send the disconnect signal to the remote instance
                                                break;
                                            }

                                            await httpClient.SendData(response, 0, response.Length, flush: false);
                                        }

                                        if (httpClient.Stream != null)
                                            await httpClient.Stream.FlushAsync();
                                    }
                                    catch (Exception e)
                                    {
                                        Log.Warning(nameof(RemoteHandler), "Exception when handling request: " + e.Message);
                                        throw;
                                    }
                                    finally
                                    {
                                        try
                                        {
                                            // Send disconnect command
                                            await SendPacket(client, 4, new byte[] { }, clientId: randomClientId);
                                        }
                                        catch { }

                                        // Clean up
                                        Log.Debug(nameof(RemoteHandler), "Removing " + randomClientId + " from our queues");
                                        
                                        lock (DataQueues)
                                            DataQueues.Remove(randomClientId);
                                    }                                    
                                });
                            }
                        }
                    }
                    else if (command == 3 && expectedLength >= sizeof(Int64)) // Incoming data to proxy back to client   [ Int64 Client Id ] + [ Data ] ,  No data means end of response
                    {                        
                        var clientId = BitConverter.ToInt64(data.IncomingBuffer, 5);
                        Log.Debug(nameof(RemoteHandler), "Receiving data for " + clientId);
                        
                        BlockingCollection<byte[]>? dataCollection = null;
                        lock (DataQueues)
                        {
                            if (DataQueues.ContainsKey(clientId))
                                dataCollection = DataQueues[clientId];
                            else
                                Log.Warning(nameof(RemoteHandler), "Error: Received data for a client (" + clientId + ") which does not exists");
                        }

                        if (dataCollection != null)
                        {
                            var actualData = new byte[expectedLength - sizeof(Int64)];
                            Buffer.BlockCopy(data.IncomingBuffer, 5 + sizeof(Int64), actualData, 0, actualData.Length);
                            dataCollection.Add(actualData);
                        }
                    }
                    else
                    {
                        Log.Warning(nameof(RemoteHandler), "Unknown command " + command);
                    }
                }

                // Move rest of buffer to start
                Buffer.BlockCopy(data.IncomingBuffer, (int)expectedLength + 5, data.IncomingBuffer, 0, data.IncomingBufferLength - ((int)expectedLength + 5));
                data.IncomingBufferLength -= ((int)expectedLength + 5);                 
            }
        }

        private static async Task SendPacket(Client client, byte command, byte[] data, int dataOffset = -1, int dataCount = -1, Int64 clientId=0)
        {
            Log.Debug(nameof(RemoteHandler), "Sending command " + command);
            
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(command);
                ms.Write(BitConverter.GetBytes((UInt32)(data.Length + (clientId != 0 ? sizeof(Int64) : 0))));
                if (clientId != 0)                
                    ms.Write(BitConverter.GetBytes(clientId));

                if (dataOffset >= 0 && dataCount >= 0)
                    ms.Write(data, dataOffset, dataCount);
                else
                    ms.Write(data);
                ms.Position = 0;
                await client.SendStream(ms, flush: false);
            }
        }

        class RemoteClientData
        {
            public required byte[] IncomingBuffer { get; set; }
            public int IncomingBufferLength { get; set; }
            public bool Authenticated { get; set; }
            public string[]? SpecificDomains { get; set; }
            public List<Tuple<string, string>> Routes { get; set; } = new List<Tuple<string, string>>();
        }
    }
}
