using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.Http.HttpHandler;

namespace Comgenie.Server.Handlers.Http
{
    public partial class HttpHandler
    {
        public void AddWebsocketRoute(string domain, string path, Func<HttpClientData, byte, byte[], ulong, ulong, Task> messageReceivedHandler, Func<HttpClientData, Task>? connectHandler = null, Func<HttpClientData, Task>? disconnectHandler = null)
        {
            AddRoute(domain, path, new Route()
            {
                HandleExecuteRequestAsync = async (client, data, cancellationToken) =>
                {
                    // Websocket route, see if the request is actually meant for a websocket 
                    if (data.Headers.ContainsKey("sec-websocket-key"))
                    {
                        // Send a special response
                        var accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(data.Headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        var response = new HttpResponse()
                        {
                            StatusCode = 101,
                            Headers = new Dictionary<string, string>()
                            {
                                { "Upgrade", "websocket" },
                                { "Connection", "Upgrade" },
                                { "Sec-WebSocket-Accept", accept }
                            },
                            ContentType = "",
                            CallbackResponseSent = connectHandler
                        };
                        data.DisconnectedHandlerAsync = disconnectHandler;
                        data.OverrideHandleClientDataAsync = async (client, httpClient) =>
                        {
                            if (data.IncomingBufferLength <= 2)
                                return false;
                            var fin = (data.IncomingBuffer[0] & 0b10000000) != 1;
                            var mask = (data.IncomingBuffer[1] & 0b10000000) != 0;
                            var opcode = data.IncomingBuffer[0] & 0b00001111;
                            ulong offset = 2;

                            var msglen = (ulong)(data.IncomingBuffer[1] & 0b01111111);
                            if (msglen == 126)
                            {
                                if (data.IncomingBufferLength < 4) // Not enough data yet
                                    return false;
                                // Longer message (2 byte len), add more bytes (note: websocket uses big-endian) 
                                msglen = (ulong)data.IncomingBuffer[2] << 8;
                                msglen += data.IncomingBuffer[3];
                                offset += 2;

                            }
                            else if (msglen == 127 && data.IncomingBufferLength >= 10)
                            {
                                if (data.IncomingBufferLength < 10) // Not enough data yet
                                    return false;
                                // Longest message (8 byte len), add more bytes 
                                msglen = (ulong)data.IncomingBuffer[2] << 56;
                                msglen += (ulong)data.IncomingBuffer[3] << 48;
                                msglen += (ulong)data.IncomingBuffer[4] << 40;
                                msglen += (ulong)data.IncomingBuffer[5] << 32;
                                msglen += (ulong)data.IncomingBuffer[6] << 24;
                                msglen += (ulong)data.IncomingBuffer[7] << 16;
                                msglen += (ulong)data.IncomingBuffer[8] << 8;
                                msglen += data.IncomingBuffer[9];
                                offset += 8;
                            }

                            if ((ulong)data.IncomingBufferLength < msglen + offset)
                                return false; // Not enough data yet

                            if (mask && msglen > 0)
                            {
                                byte[] masks = new byte[4] { data.IncomingBuffer[offset], data.IncomingBuffer[offset + 1], data.IncomingBuffer[offset + 2], data.IncomingBuffer[offset + 3] };
                                offset += 4;

                                if ((ulong)data.IncomingBufferLength < msglen + offset)
                                    return false; // Not enough data yet

                                for (ulong i = 0; i < msglen; i++)
                                    data.IncomingBuffer[offset + i] = (byte)(data.IncomingBuffer[offset + i] ^ masks[i % 4]);
                            }

                            // TODO: Support fin
                            if (!fin)
                            {
                                Log.Warning(nameof(HttpHandler), "Unsupported websocket option: fin = 0");
                                // TODO
                            }
                            else
                            {
                                // Got all data in offset, with msglen
                                client.ResetTimeout(new TimeSpan(0, 30, 0)); 

                                if (opcode == 0x09) // ping
                                {
                                    await data.SendWebsocketMessage(0x0A, data.IncomingBuffer, offset, msglen, cancellationToken);
                                }
                                else if (opcode == 0x01 || opcode == 0x02) // 0x01 text or 0x02 binary
                                {
                                    await messageReceivedHandler(data, (byte)opcode, data.IncomingBuffer, offset, msglen);
                                }
                                else if (opcode == 0x08) // closing
                                {
                                    // TODO, body contents is reason of closing
                                    // Just echo for now
                                    await data.SendWebsocketMessage(0x08, data.IncomingBuffer, offset, msglen, cancellationToken);
                                }
                                else
                                {
                                    Log.Warning(nameof(HttpHandler), "Unsupported websocket opcode: " + opcode);
                                }
                            }

                            // Message handled, remove from buffer
                            Buffer.BlockCopy(data.IncomingBuffer, (int)(offset + msglen), data.IncomingBuffer, 0, data.IncomingBufferLength - (int)(msglen + offset));
                            data.IncomingBufferLength -= (int)(msglen + offset);
                            return true;
                        };
                        return response;
                    }

                    return new HttpResponse()
                    {
                        StatusCode = 400,
                        ContentType = "text/plain",
                        Data = Encoding.UTF8.GetBytes("Invalid websocket request")
                    };
                }
            });
        }
    }
}
