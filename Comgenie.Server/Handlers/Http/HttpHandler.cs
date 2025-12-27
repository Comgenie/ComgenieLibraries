using Comgenie.Server.Utils;
using Comgenie.Util;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Comgenie.Server.Handlers.Http
{
    public partial class HttpHandler : IConnectionHandler
    {
        internal Dictionary<string, Route> Routes = new Dictionary<string, Route>();
        private Dictionary<string, string> DomainAliases = new Dictionary<string, string>(); // Alias domain -> main domain        
        private List<Action<HttpClientData, HttpResponse>> PostProcessors = new List<Action<HttpClientData, HttpResponse>>();

        // Enable GZip compression for static text files 
        public string[] EnableGZipCompressionContentTypes = new string[] { "text/plain", "application/json", "text/html", "image/svg+xml", "application/xml", "text/css", "text/javascript" };

        public Task ClientConnect(Client client)
        {
            client.Data = new HttpClientData()
            {
                Client = client,
                IncomingBuffer = new byte[1024 * 514]
            };
            return Task.CompletedTask;
        }

        public async Task ClientDisconnect(Client client)
        {
            if (client.Data == null)
                return;
            var data = (HttpClientData)client.Data;
            if (data.DisconnectedHandlerAsync != null)
                await data.DisconnectedHandlerAsync(data);
        }

        public void AddPostProcessor(Action<HttpClientData, HttpResponse> postProcessor)
        {
            PostProcessors.Add(postProcessor);
        }

        public void AddAlias(string aliasDomain, string mainDomain)
        {
            aliasDomain = aliasDomain.ToLower();
            mainDomain = mainDomain.ToLower();

            if (!DomainAliases.ContainsKey(aliasDomain))
                DomainAliases.Add(aliasDomain, mainDomain);
            else
                DomainAliases[aliasDomain] = mainDomain;
        }

        public async Task ClientReceiveData(Client client, byte[] buffer, int len)
        {
            var data = (HttpClientData?)client.Data;

            if (data == null || data.IncomingBufferLength + len > data.IncomingBuffer.Length)
                return; // Buffer too small

            Buffer.BlockCopy(buffer, 0, data.IncomingBuffer, data.IncomingBufferLength, len);

            data.IncomingBufferLength += len;
            while (data.IncomingBufferLength > 1)
            {
                if (data.OverrideHandleClientDataAsync != null)
                {
                    if (!await data.OverrideHandleClientDataAsync(data))
                        break;
                    continue;
                }

                if (data.RequestRaw == null)
                {
                    if (data.IncomingBufferLength <= 2)
                        break;
                    // Still receiving the header
                    for (var i = 2; i < data.IncomingBufferLength; i++)
                    {
                        if (data.IncomingBuffer[i] == '\n' && data.IncomingBuffer[i - 2] == '\n')
                        {
                            // Full header received. Parse the header                            
                            var startByte = 0;
                            var headerName = "";
                            var curLineStart = 0;
                            var curValueStart = 0;
                            for (var j = 0; j < i; j++)
                            {
                                if (data.RequestRaw == null)
                                {
                                    if (data.IncomingBuffer[j] == ' ' && startByte == 0)
                                    {
                                        data.Method = Encoding.ASCII.GetString(data.IncomingBuffer, 0, j);
                                        startByte = j + 1;
                                    }
                                    else if (data.IncomingBuffer[j] == ' ' && startByte > 0)
                                    {
                                        data.RequestRaw = Encoding.ASCII.GetString(data.IncomingBuffer, startByte, j - startByte);
                                        while (data.IncomingBuffer[j] != '\r' && j < i) // Fast forward to the line break
                                            j++;
                                        curLineStart = j + 2;
                                    }

                                }
                                else if (headerName == "" && data.IncomingBuffer[j] == ':' && j > curLineStart)
                                {
                                    // Got a header name
                                    headerName = Encoding.ASCII.GetString(data.IncomingBuffer, curLineStart, j - curLineStart);
                                    curValueStart = j + 2;
                                    j++; // We can ignore the next character (space)
                                }
                                else if (data.IncomingBuffer[j] == '\r' && headerName != "" && j > curValueStart)
                                {
                                    // Got a header value\
                                    var headerNameLower = headerName.ToLower();
                                    var headerValue = Encoding.ASCII.GetString(data.IncomingBuffer, curValueStart, j - curValueStart);
                                    if (!data.Headers.ContainsKey(headerNameLower))
                                        data.Headers.Add(headerNameLower, headerValue);
                                    data.FullRawHeaders.Add(new KeyValuePair<string, string>(headerName, headerValue));
                                    headerName = "";
                                    curLineStart = j + 2;
                                    j++;// We can ignore the next character ( \n )
                                }
                            }

                            data.Host = "";
                            if (data.Headers.ContainsKey("host"))
                                data.Host = data.Headers["host"];
                            if (data.Headers.ContainsKey("content-type"))
                                data.ContentType = data.Headers["content-type"];

                            if (data.Headers.ContainsKey("content-length"))
                                if (long.TryParse(data.Headers["content-length"], out long tmpContentLength))
                                    data.ContentLength = tmpContentLength;

                            if (data.ContentLength > 0)
                            {
                                Log.Debug(nameof(HttpHandler), "Receiving post data of " + data.ContentLength + " bytes");

                                if (data.ContentLength < 1024 * 1024 * 10)
                                {
                                    // If its 10 MB or less, we will keep it in memory
                                    data.Data = new byte[data.ContentLength];
                                    data.DataTempFileName = null;
                                    data.DataStream = null;
                                }
                                else // Otherwise, stream it to disk
                                {
                                    data.Data = null;
                                    data.DataTempFileName = Path.GetTempFileName();
                                    data.DataStream = File.OpenWrite(data.DataTempFileName);
                                }
                            }

                            i++; // Start content or next request
                            if (i < data.IncomingBufferLength)
                            {
                                // We're moving the rest to the front (removing the headers in this case)
                                Buffer.BlockCopy(data.IncomingBuffer, i, data.IncomingBuffer, 0, data.IncomingBufferLength - i);
                                data.IncomingBufferLength -= i;
                            }
                            else
                            {
                                data.IncomingBufferLength = 0;
                            }
                            break;
                        }

                        if (i + 1 == data.IncomingBufferLength)
                        {
                            Log.Debug(nameof(HttpHandler), "We haven't received a full HTTP header yet. Not doing anything");
                            return; // We can't do anything with this header yet
                        }
                    }
                }

                // TODO: Support Transfer-Encoding from client
                if (data.IncomingBufferLength > 0 && (data.Data != null || data.DataStream != null) && data.DataLength < data.ContentLength)
                {
                    // Add to the buffer
                    var dataExpecting = data.ContentLength - data.DataLength;

                    if (data.DataStream != null)
                    {
                        // Stream to file
                        if (data.IncomingBufferLength >= dataExpecting)
                        {
                            // There is more than or exactly the data waiting that we are expecting
                            await data.DataStream.WriteAsync(data.IncomingBuffer, 0, (int)dataExpecting);
                            data.IncomingBufferLength -= (int)dataExpecting;
                            data.DataLength = data.ContentLength;
                        }
                        else
                        {
                            // There is less data waiting
                            await data.DataStream.WriteAsync(data.IncomingBuffer, 0, data.IncomingBufferLength);
                            data.DataLength += data.IncomingBufferLength;
                            data.IncomingBufferLength = 0;
                        }
                    }
                    else if (data.Data != null)
                    {
                        // Add to byte array
                        if (data.IncomingBufferLength >= dataExpecting)
                        {
                            // There is more than or exactly the data waiting that we are expecting
                            Buffer.BlockCopy(data.IncomingBuffer, 0, data.Data, (int)data.DataLength, (int)dataExpecting);
                            data.IncomingBufferLength -= (int)dataExpecting;
                            data.DataLength = data.ContentLength;
                            Buffer.BlockCopy(data.IncomingBuffer, (int)dataExpecting, data.IncomingBuffer, 0, data.IncomingBufferLength); // Move the rest of the buffer to the front
                        }
                        else
                        {
                            // There is less data waiting
                            Buffer.BlockCopy(data.IncomingBuffer, 0, data.Data, (int)data.DataLength, data.IncomingBufferLength);
                            data.DataLength += data.IncomingBufferLength;
                            data.IncomingBufferLength = 0;
                        }
                    }
                }

                if (data.RequestRaw != null && (data.Data == null && data.DataStream == null || data.DataLength == data.ContentLength))
                {
                    // Have the posted data available as stream
                    if (data.DataStream == null && data.Data != null)
                    {
                        data.DataStream = new MemoryStream(data.Data);
                    }
                    else if (data.DataStream != null && data.DataTempFileName != null)
                    {
                        data.DataStream.Close();
                        data.DataStream = File.OpenRead(data.DataTempFileName);
                    }

                    try
                    {
                        await ExecuteRequest(client, data);
                    }
                    catch (Exception e)
                    {
                        var content = "An error happened while executing this request. (" + data.Client.RemoteAddress + ")";
                        Console.WriteLine(e.Message + "\r\n" + e.StackTrace);
                        if (data.Client.RemoteAddress.StartsWith("192.168.") || data.Client.RemoteAddress.StartsWith("127.0."))
                        {

                            content += "\r\n" + e.Message + "\r\n" + e.StackTrace;
                        }

                        var tmpResponse = Encoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Type: text/plain\r\nContent-Length: " + content.Length + "\r\n\r\n" + content);
                        try
                        {
                            await client.SendData(tmpResponse, 0, tmpResponse.Length);
                        }
                        catch { }
                    }
                    finally
                    {
                        try
                        {
                            if (data.DataStream != null)
                                data.DataStream.Close();
                            if (data.DataTempFileName != null)
                                File.Delete(data.DataTempFileName);
                        }
                        catch { }
                    }

                    // Reset
                    data.Request = null;
                    data.RequestRaw = null;
                    data.ContentLength = 0;
                    data.ContentType = null;
                    data.DataLength = 0;
                    data.Data = null;
                    data.Host = "";
                    data.Method = null;
                    data.DataTempFileName = null;
                    data.DataStream = null;
                    data.Headers.Clear();
                    data.FullRawHeaders.Clear();
                    data.FileData = null;
                }
            }
        }

        private async Task ExecuteRequest(Client client, HttpClientData data)
        {
            data.Request = System.Web.HttpUtility.UrlDecode(data.RequestRaw);
            Log.Debug(nameof(HttpHandler), "Got request from " + client.Socket?.RemoteEndPoint?.ToString() + " for " + data.Host + ": " + data.Request);

            if (data.Request == null || data.RequestRaw == null)
                return;

            // Find out what domain we are requesting
            data.Host = data.Host.ToLower();
            if (DomainAliases.ContainsKey(data.Host))
                data.Host = DomainAliases[data.Host];
            if (client.Server != null && !client.Server.Domains.Contains(data.Host))
                data.Host = client.Server.DefaultDomain ?? data.Host;
            Log.Debug(nameof(HttpHandler), "Using host " + data.Host);

            HttpResponse? response = null;

            // Query string without parameters
            var parameterStart = data.Request.IndexOf("?");
            data.RequestPage = parameterStart > 0 ? data.Request.Substring(0, parameterStart) : data.Request;

            // All data received, we can respond!
            Route? route = null;
            var routeKey = data.Host + data.RequestPage;
            Log.Info(nameof(HttpHandler), client.RemoteAddress + " - Full route lookup:" + data.Host + data.RequestPage);

            if (Routes.ContainsKey(routeKey))
            {
                Log.Debug(nameof(HttpHandler), "Found exact match");
                route = Routes[routeKey];
                data.RequestPageShort = ""; // RequestPage without the route prefix
            }
            else
            {
                // No exact route match, see if we got any 'catch all' routes 
                var tmp = data.Host + data.RequestPage;
                while (tmp.IndexOf("/") > 0 && route == null)
                {
                    tmp = tmp.Substring(0, tmp.LastIndexOf("/"));
                    routeKey = tmp + "/*";
                    if (Routes.ContainsKey(routeKey))
                        route = Routes[routeKey];
                }

                if (route != null)
                    data.RequestPageShort = data.RequestPage.Substring(tmp.Length - data.Host.Length + 1); // RequestPage without the route prefix
            }

            try
            {
                if (route?.HandleExecuteRequestAsync != null)
                {
                    response = await route.HandleExecuteRequestAsync(client, data);
                }
                else if (route?.HandleExecuteRequest != null)
                {
                    response = route.HandleExecuteRequest(client, data);
                }
            }
            catch (Exception e)
            {
                response = new HttpResponse()
                {
                    StatusCode = 500,
                    ContentType = "text/plain",
                    Data = Encoding.UTF8.GetBytes("Handle error: " + e.Message)
                };
            }

            if (response?.ResponseFinished == true)
            {
                foreach (var processor in PostProcessors)
                    processor(data, response);

                Log.Info(nameof(HttpHandler), "Forwarded response: " + response.StatusCode);

                return; // Remote connection proxy actually forwarded the full http response from the remote instance by now
            }

            if (response == null)
            {
                response = new HttpResponse()
                {
                    StatusCode = 404,
                    ContentType = "text/plain",
                    Data = Encoding.UTF8.GetBytes("Not found")
                };
            }

            if (response.FileName != null)
            {
                var fileSize = new FileInfo(response.FileName).Length;

                if (data.Headers.ContainsKey("range") && data.Headers["range"].StartsWith("bytes=") && fileSize > 0)
                {
                    var bytes = data.Headers["range"].Substring(6).Split('-'); // 0-1023  // Start byte-end byte
                    long startByte = 0;
                    long endByte = fileSize - 1;
                    if (bytes.Length > 1)
                    {
                        if (!string.IsNullOrWhiteSpace(bytes[0]))
                            startByte = long.Parse(bytes[0]);
                        if (!string.IsNullOrWhiteSpace(bytes[1]))
                            endByte = long.Parse(bytes[1]);
                    }
                    if (startByte >= fileSize)
                        startByte = fileSize - 1;
                    if (endByte >= fileSize)
                        endByte = fileSize - 1;
                    if (startByte > endByte)
                        startByte = endByte;

                    Log.Debug(nameof(HttpHandler), "Sending response partial: " + startByte + "-" + endByte + "/" + fileSize);
                    response.StatusCode = 206; // Partial
                    response.Stream = File.OpenRead(response.FileName);
                    response.ContentOffsetStream = startByte;
                    response.ContentLengthStream = endByte - startByte + 1;
                    response.Headers.Add("Content-Range", "bytes " + startByte + "-" + endByte + "/" + fileSize);
                }
                else
                {
                    // Full file
                    response.StatusCode = 200;
                    response.Stream = File.OpenRead(response.FileName);
                    response.ContentLengthStream = fileSize;
                    //response.GZipResponse = EnableGZipCompressionContentTypes != null && EnableGZipCompressionContentTypes.Contains(Path.GetExtension(response.FileName).ToLower());
                    response.Headers.Add("Cache-Control", "public, max-age=86400");
                }

                if (response.ContentType == null)
                    response.ContentType = ContentTypeUtil.GetContentTypeFromFileName(response.FileName);

                // Allow ranges
                response.Headers.Add("Accept-Ranges", "bytes");
            }
            else if (response.ResponseObject != null)
            {
                //var testData = JsonSerializer.Serialize(response.ResponseObject);
                var ms = new MemoryStream();
                JsonSerializer.Serialize(ms, response.ResponseObject);
                response.ContentLengthStream = ms.Position;
                ms.Position = 0;

                response.Data = null;
                response.Stream = ms;

                //response.Data = ASCIIEncoding.UTF8.GetBytes(JsonSerializer.Serialize(response.ResponseObject));
                if (response.ContentType == null)
                    response.ContentType = "application/json;charset=UTF-8";
            }

            // Check if we return any content type we want to gzip compress (note that http application may also set this flag)
            if (!response.GZipResponse && response.ContentType != null && EnableGZipCompressionContentTypes != null && EnableGZipCompressionContentTypes.Any(a => response.ContentType.StartsWith(a)))
            {
                response.GZipResponse = true;
            }

            if (response.StatusCode == 0)
                response.StatusCode = 200;


            foreach (var processor in PostProcessors)
                processor(data, response);

            var codeText = ((HttpStatusCode)response.StatusCode).ToString();

            StringBuilder sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {response.StatusCode} {codeText}\r\n");
            if (response.ContentType != "")
                sb.Append($"Content-Type: {(response.ContentType ?? "text/html")}\r\n");


            // Check if we can actually return the response with gzip compression
            if (response.GZipResponse && data.Headers.ContainsKey("accept-encoding") && data.Headers["accept-encoding"].Contains("gzip") && response.Data == null && response.Stream != null)
            {
                sb.Append("Content-Encoding: gzip\r\n");
                response.ChunkedResponse = true; // required as we don't know the final transfer content length yet
            }
            else
            {
                response.GZipResponse = false;
            }

            if ((response.ChunkedResponse || response.ContentLengthStream < 0) && response.Data == null && response.Stream != null)
            {
                sb.Append("Transfer-Encoding: chunked\r\n");
                response.Stream = new ChunkedStream(response.Stream, response.GZipResponse);
                response.ContentLengthStream = -1;
            }
            else
            {
                if (response.ContentType != "")
                    sb.Append($"Content-Length: {(response.Data != null ? response.Data.Length : response.Stream != null ? response.ContentLengthStream : 0)}\r\n");
            }

            if (response.Headers != null)
            {
                foreach (var header in response.Headers)
                    sb.Append($"{header.Key}: {header.Value}\r\n");
            }

            sb.Append("\r\n");

            var tmpResponseHeader = Encoding.ASCII.GetBytes(sb.ToString());

            Log.Info(nameof(HttpHandler), "Got response: " + response.StatusCode);

            if (data.Method == "HEAD")
            {
                // Just return the headers
                await client.SendData(tmpResponseHeader, 0, tmpResponseHeader.Length);

                if (response.Stream != null)
                {
                    try
                    {
                        response.Stream.Close();
                        response.Stream.Dispose();
                    }
                    catch { }
                }
                return;
            }

            if (response.Data != null)
            {
                var tmpResponse = new byte[tmpResponseHeader.Length + response.Data.Length];
                Buffer.BlockCopy(tmpResponseHeader, 0, tmpResponse, 0, tmpResponseHeader.Length);
                Buffer.BlockCopy(response.Data, 0, tmpResponse, tmpResponseHeader.Length, response.Data.Length);
                await client.SendData(tmpResponse, 0, tmpResponse.Length);
            }
            else if (response.Stream != null)
            {
                await client.SendData(tmpResponseHeader, 0, tmpResponseHeader.Length, false);
                await client.SendStream(response.Stream, response.ContentOffsetStream, response.ContentLengthStream);
            }
            else
            {
                await client.SendData(tmpResponseHeader, 0, tmpResponseHeader.Length); // Empty
            }

            if (response.CallbackResponseSent != null)
                await response.CallbackResponseSent(data);

        }
        private void GetParametersFromQueryString(Dictionary<string, string> resultDict, string queryString)
        {
            var curPos = 0;
            string? name = null;
            while (curPos < queryString.Length)
            {
                var nextPos = queryString.IndexOf(name == null ? "=" : "&", curPos);
                if (nextPos < 0)
                {
                    if (name != null)
                    {
                        if (resultDict.ContainsKey(name))
                            resultDict[name] = WebUtility.UrlDecode(queryString.Substring(curPos));
                        else
                            resultDict.Add(name, WebUtility.UrlDecode(queryString.Substring(curPos)));
                    }
                    break;
                }

                if (name == null)
                    name = queryString.Substring(curPos, nextPos - curPos);
                else
                {
                    if (resultDict.ContainsKey(name))
                        resultDict[name] = WebUtility.UrlDecode(queryString.Substring(curPos, nextPos - curPos));
                    else
                        resultDict.Add(name, WebUtility.UrlDecode(queryString.Substring(curPos, nextPos - curPos)));
                    name = null;
                }

                curPos = nextPos + 1;
            }
        }
        
        internal void AddCustomRoute(string domain, string path, Func<Client, HttpClientData, Task<HttpResponse?>> handleCallback)
        {
            AddRoute(domain, path, new Route()
            {
                HandleExecuteRequestAsync = handleCallback
            });
        }
        private void AddRoute(string domain, string path, Route route) // TODO: Support having multiple routes at the same time active (load balancing)
        {
            domain = domain.ToLower();
            if (DomainAliases.ContainsKey(domain))
                domain = DomainAliases[domain];
            // Remove any / at the start of the path
            while (path.Length > 0 && path[0] == '/')
                path = path.Substring(1);

            if (!Routes.ContainsKey(domain + "/" + path))
                Routes.Add(domain + "/" + path, route);
            else
                Routes[domain + "/" + path] = route;
        }
        public void RemoveRoute(string domain, string path)
        {
            while (path.Length > 0 && path[0] == '/')
                path = path.Substring(1);
            if (Routes.ContainsKey(domain + "/" + path))
                Routes.Remove(domain + "/" + path);
        }

        internal class Route
        {
            public Func<Client, HttpClientData, HttpResponse?>? HandleExecuteRequest { get; set; }
            public Func<Client, HttpClientData, Task<HttpResponse?>>? HandleExecuteRequestAsync { get; set; }
        }
    }
}
