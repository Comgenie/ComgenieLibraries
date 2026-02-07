using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.Http.HttpHandler;

namespace Comgenie.Server.Handlers.Http
{
    /// <summary>
    /// For each connection handled by the HttpHandler, an associated HttpClientData object will be created. This is responsible for storing the buffer as well as all of the http request data.
    /// Most of the fields are reset between each http request.
    /// </summary>
    public class HttpClientData
    {
        /// <summary>
        /// Reference to the client associated to this http data object
        /// </summary>
        public required Client Client { get; set; }

        /// <summary>
        /// Raw (and unprocessed) incoming data buffer
        /// </summary>
        public required byte[] IncomingBuffer { get; set; }

        /// <summary>
        /// Total length of the current incoming data buffer
        /// </summary>
        public int IncomingBufferLength { get; set; }

        /// <summary>
        /// HTTP Method used for this quest as provided by the client.
        /// </summary>
        public string? Method { get; set; }

        /// <summary>
        /// Full undecoded request (starting with / )
        /// </summary>
        public string? RequestRaw { get; set; }

        /// <summary>
        /// Full decoded request (starting with / )
        /// </summary>
        public string? Request { get; set; }

        /// <summary>
        /// Request without query string parameters
        /// </summary>
        public string? RequestPage { get; set; }

        /// <summary>
        /// Request without the query string parameters and the route prefix
        /// </summary>
        public string? RequestPageShort { get; set; }

        /// <summary>
        /// Host for thie request. Note that this is the Host as decided by the HttpHandler. This means that the domains added as aliases are already applied, and if the host is not in the domain list of the server, the default domain is used.
        /// The user provided host can be found within the Headers.
        /// </summary>
        public string Host { get; set; } = "";

        /// <summary>
        /// Headers but only the first header with each key is stored. Note that all keys are lowercased.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// A list of all provided headers, included ones with duplicated keys. Note that the keys are still lowercased.
        /// </summary>
        public List<KeyValuePair<string, string>> FullRawHeaders { get; set; } = new List<KeyValuePair<string, string>>();

        /// <summary>
        /// Content length of any posted data by the client.
        /// </summary>
        public long ContentLength { get; set; }

        /// <summary>
        /// Content type as provided by the client.
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Small storage for posted data, this one is only used for request smaller than 10 MB
        /// </summary>
        internal byte[]? Data { get; set; }

        /// <summary>
        /// Stream to access posted data, this stream is seekable.
        /// </summary>
        public Stream? DataStream { get; set; }

        /// <summary>
        /// Temp file name only used for requests > 10 MB. Note that this file is automatically removed after the request has been processed.
        /// </summary>
        public string? DataTempFileName { get; set; }

        /// <summary>
        /// Length of the data received. When processing a fully received http request, this one should be the same as the content-length.
        /// </summary>
        public long DataLength { get; set; }

        /// <summary>
        /// Uploaded files by the client. Note that the filenames are the ones provided by the client. Use the .GetStream method to access the data of the uploaded files.
        /// </summary>
        public List<HttpClientFileData>? FileData { get; set; }

        /// <summary>
        /// Optional callback handler which gets called whenever the connection of the client gets closed. Note that browsers do leave their connection open for a few minutes if the user is still on the site, and that multiple requests will reuse the same connection.
        /// </summary>
        public Func<HttpClientData, Task>? DisconnectedHandlerAsync { get; set; }

        /// <summary>
        /// Handler to take over the buffer processing. This is used in situations where the HTTP connection gets upgraded to a different type of protocol, like Websockets do.
        /// </summary>
        public Func<Client, HttpClientData, Task<bool>>? OverrideHandleClientDataAsync { get; set; }

        /// <summary>
        /// Helper to directly get the value of a cookie with the provided cookie name. 
        /// </summary>
        /// <param name="cookieName">Name of the cookie to retrieve the value from</param>
        /// <returns>The value if present, or null if not present</returns>
        public string? CookieValue(string cookieName)
        {
            foreach (var header in FullRawHeaders)
            {
                if (header.Key.ToLower() != "cookie")
                    continue;

                var cookies = header.Value.Split(';');
                foreach (var cookie in cookies)
                {
                    var singleCookie = cookie.Trim().Split('=', 2);
                    if (singleCookie[0] == cookieName && singleCookie.Length == 2)
                        return singleCookie[1];
                }
            }
            return null;
        }


        public async Task SendWebsocketMessage(byte opcode, byte[] data, ulong offset, ulong len, CancellationToken cancellationToken = default)
        {
            byte[] header;
            if (len < 126)
            {
                header = new byte[2];
                header[1] = (byte)len;
            }
            else if (len < 65536)
            {
                header = new byte[4];
                header[0] |= 0b10000000; // set fin flag
                header[1] = 126;
                header[2] = (byte)(len >> 8);
                header[3] = (byte)len;
            }
            else
            {
                header = new byte[10];
                header[0] |= 0b10000000; // set fin flag
                header[1] = 127;
                header[2] = (byte)(len >> 56);
                header[3] = (byte)(len >> 48);
                header[4] = (byte)(len >> 40);
                header[5] = (byte)(len >> 32);
                header[6] = (byte)(len >> 24);
                header[7] = (byte)(len >> 16);
                header[8] = (byte)(len >> 8);
                header[9] = (byte)len;
            }

            header[0] = opcode;
            header[0] |= 0b10000000; // set fin flag

            await Client.SendDataAsync(header, 0, header.Length, false, cancellationToken);
            await Client.SendDataAsync(data, (int)offset, (int)len, true, cancellationToken);
        }
        public async Task SendWebsocketMessage(byte opcode, byte[] data, CancellationToken cancellationToken = default)
        {
            await SendWebsocketMessage(opcode, data, 0, (ulong)data.Length, cancellationToken);
        }
        public async Task SendWebsocketText(string text, CancellationToken cancellationToken = default)
        {
            await SendWebsocketMessage(0x01, Encoding.UTF8.GetBytes(text), cancellationToken);
        }
        public async Task SendWebsocketJsonObject(object obj, CancellationToken cancellationToken = default)
        {
            await SendWebsocketMessage(0x01, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj)), cancellationToken);
        }
    }
}
