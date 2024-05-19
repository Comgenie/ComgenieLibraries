using Comgenie.Server.Utils;
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

namespace Comgenie.Server.Handlers
{
    public class HttpHandler : IConnectionHandler
    {
        internal Dictionary<string, Route> Routes = new Dictionary<string, Route>();
        private Dictionary<string, string> DomainAliases = new Dictionary<string, string>(); // Alias domain -> main domain        
        private List<Action<HttpClientData, HttpResponse>>? PostProcessors = null;

        // Enable GZip compression for static text files 
        public string[] EnableGZipCompressionContentTypes = new string[] { "text/plain", "application/json", "text/html", "image/svg+xml", "application/xml", "text/css", "text/javascript" };

        public Task ClientConnect(Client client)
        {
            client.Data = new HttpClientData()
            {
                Client = client,
                IncomingBuffer = new byte[1024*514]
            };
            return Task.CompletedTask;
        }

        public async Task ClientDisconnect(Client client)
        {
            if (client.Data == null)
                return;
            var data = (HttpClientData)client.Data;
            if (data.WebsocketDisconnectedHandler != null)
                await data.WebsocketDisconnectedHandler(data);
        }

        public void AddPostProcessor(Action<HttpClientData, HttpResponse> postProcessor)
        {
            if (PostProcessors == null)
                PostProcessors = new List<Action<HttpClientData, HttpResponse>>();
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
                if (data.WebsocketFrameHandler != null)
                {
                    if (data.IncomingBufferLength <= 2)
                        break;
                    var fin = (data.IncomingBuffer[0] & 0b10000000) != 1;
                    var mask = (data.IncomingBuffer[1] & 0b10000000) != 0;
                    var opcode = (data.IncomingBuffer[0] & 0b00001111);
                    ulong offset = 2;

                    var msglen = (ulong)(data.IncomingBuffer[1] & 0b01111111);
                    if (msglen == 126)
                    {
                        if (data.IncomingBufferLength < 4) // Not enough data yet
                            break;
                        // Longer message (2 byte len), add more bytes (note: websocket uses big-endian) 
                        msglen = ((ulong)data.IncomingBuffer[2]) << 8;
                        msglen += ((ulong)data.IncomingBuffer[3]);
                        offset += 2;
                        
                    }
                    else if (msglen == 127 && data.IncomingBufferLength >= 10)
                    {
                        if (data.IncomingBufferLength < 10) // Not enough data yet
                            break;
                        // Longest message (8 byte len), add more bytes 
                        msglen = ((ulong)data.IncomingBuffer[2]) << 56;
                        msglen += ((ulong)data.IncomingBuffer[3]) << 48;
                        msglen += ((ulong)data.IncomingBuffer[4]) << 40;
                        msglen += ((ulong)data.IncomingBuffer[5]) << 32;
                        msglen += ((ulong)data.IncomingBuffer[6]) << 24;
                        msglen += ((ulong)data.IncomingBuffer[7]) << 16;
                        msglen += ((ulong)data.IncomingBuffer[8]) << 8;
                        msglen += ((ulong)data.IncomingBuffer[9]);
                        offset += 8;
                    }

                    if ((ulong)data.IncomingBufferLength < msglen + offset)
                        break; // Not enough data yet

                    if (mask && msglen > 0)
                    {
                        byte[] masks = new byte[4] { data.IncomingBuffer[offset], data.IncomingBuffer[offset + 1], data.IncomingBuffer[offset + 2], data.IncomingBuffer[offset + 3] };
                        offset += 4;

                        if ((ulong)data.IncomingBufferLength < msglen + offset)
                            break; // Not enough data yet

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
                        if (opcode == 0x09) // ping
                        {
                            await data.SendWebsocketMessage(0x0A, data.IncomingBuffer, offset, msglen);
                        }
                        else if (opcode == 0x01 || opcode == 0x02) // 0x01 text or 0x02 binary
                        {
                            await data.WebsocketFrameHandler(data, (byte)opcode, data.IncomingBuffer, offset, msglen);
                        }
                        else if (opcode == 0x08) // closing
                        {
                            // TODO, body contents is reason of closing
                            // Just echo for now
                            await data.SendWebsocketMessage(0x08, data.IncomingBuffer, offset, msglen);
                        }
                        else
                        {
                            Log.Warning(nameof(HttpHandler), "Unsupported websocket opcode: " + opcode);
                        }
                    }

                    // Message handled, remove from buffer
                    Buffer.BlockCopy(data.IncomingBuffer, (int)(offset + msglen), data.IncomingBuffer, 0, data.IncomingBufferLength - (int)(msglen + offset));
                    data.IncomingBufferLength -= (int)(msglen + offset);
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
                            for (var j=0;j<i;j++)
                            {
                                if (data.RequestRaw == null)
                                {
                                    if (data.IncomingBuffer[j] == ' ' && startByte == 0)
                                    {
                                        data.Method = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, 0, j);
                                        startByte = j + 1;
                                    }
                                    else if (data.IncomingBuffer[j] == ' ' && startByte > 0)
                                    {
                                        data.RequestRaw = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, startByte, j - startByte);
                                        while (data.IncomingBuffer[j] != '\r' && j < i) // Fast forward to the line break
                                            j++;
                                        curLineStart = j + 2;
                                    }

                                }
                                else if (headerName == "" && data.IncomingBuffer[j] == ':' && j > curLineStart)
                                {
                                    // Got a header name
                                    headerName = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, curLineStart, j - curLineStart);
                                    curValueStart = j + 2;
                                    j++; // We can ignore the next character (space)
                                }
                                else if (data.IncomingBuffer[j] == '\r' && headerName != "" && j > curValueStart) 
                                {
                                    // Got a header value\
                                    var headerNameLower = headerName.ToLower();
                                    var headerValue = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, curValueStart, j - curValueStart);
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

                if (data.RequestRaw != null && ((data.Data == null && data.DataStream == null) || data.DataLength == data.ContentLength))
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
                        var content = "An error happened while executing this request. (" + data.Client.RemoteAddress +")";
                        if (data.Client.RemoteAddress.StartsWith("192.168.") || data.Client.RemoteAddress.StartsWith("127.0."))
                        {
                            content += "\r\n" +  e.Message + "\r\n" + e.StackTrace;
                        }

                        var tmpResponse = ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Type: text/plain\r\nContent-Length: "+ content.Length+"\r\n\r\n" + content);
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

            // TEMP Code
            if (data.Request.ToLower().Contains(".php"))
            {
                Log.Warning(nameof(HttpHandler), "Added " + client.RemoteAddress + " to ban list");
                Server.IPBanList.Add(client.RemoteAddress);
                Server.SaveBanList();
                return;
            }

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
            Log.Info(nameof(HttpHandler), client.RemoteAddress +" - Full route lookup:" + data.Host + data.RequestPage);
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
                    data.RequestPageShort = data.RequestPage.Substring((tmp.Length - data.Host.Length) + 1); // RequestPage without the route prefix
            }

            if (route != null)
            {
                if (route.Contents != null)
                {
                    response = new HttpResponse()
                    {
                        StatusCode = 200,
                        ContentType = route.ContentType,
                        Data = route.Contents                        
                    };
                }
                else if (route.LocalPath != null && data.RequestPageShort != null)
                {
                    string? requestedFile = null;
                    if (Directory.Exists(route.LocalPath)) // Route is linking to a directory
                    {
                        // Combine our LocalPath with RequestPageShort to see if the request points to a file
                        foreach (var c in Path.GetInvalidPathChars())
                        {
                            if (c != '/' && c != '\'')
                                data.RequestPageShort = data.RequestPageShort.Replace(c, '-');
                        }

                        // First check against escaping the content folder
                        while (data.RequestPageShort.Contains("../") || data.RequestPageShort.Contains("..\\"))
                            data.RequestPageShort = data.RequestPageShort.Replace("../", "").Replace("..\\", "");

                        var requestedLocalPath = Path.Combine(route.LocalPath, data.RequestPageShort);

                        // Second check against escaping the content folder
                        if (!Path.GetFullPath(requestedLocalPath).StartsWith(Path.GetFullPath(route.LocalPath)))
                        {
                            Log.Error(nameof(HttpHandler), "Invalid path requested! {0}", requestedLocalPath);
                        }
                        else
                        {
                            if (File.Exists(requestedLocalPath))
                                requestedFile = requestedLocalPath;
                            else if (Directory.Exists(requestedLocalPath))
                            {
                                // Check if the directory has an index.html
                                if (File.Exists(Path.Combine(requestedLocalPath, "index.html")))
                                    requestedFile = Path.Combine(requestedLocalPath, "index.html");
                            }
                        }
                    }
                    else if (File.Exists(route.LocalPath)) // Route is linking to a file directly
                        requestedFile = route.LocalPath;

                    if (requestedFile != null)
                    {
                        response = new HttpResponse()
                        {
                            FileName = requestedFile
                        };
                    }
                }
                else if (route.Application != null && route.ApplicationMethod != null && route.ApplicationMethodParameters != null) 
                {                    
                    // Parse arguments
                    Dictionary<string, string> rawParameters = new Dictionary<string, string>();
                    if (parameterStart > 0)
                    {
                        // GET parameters
                        GetParametersFromQueryString(rawParameters, data.Request.Substring(parameterStart + 1));
                    }

                    if (data.DataStream != null && data.DataStream.Length > 0)
                    {
                        // POST parameters                                
                        if (data.ContentType != null && data.ContentType.StartsWith("application/json") && data.DataLength < 1024 * 1024 * 100)
                        {
                            // Parse as json
                            try
                            {
                                var items = JsonSerializer.Deserialize<Dictionary<string, object>>(data.DataStream);
                                if (items != null)
                                {
                                    foreach (var item in items)
                                    {
                                        if (item.Value != null)
                                            rawParameters.Add(item.Key, item.Value.ToString()!);
                                    }
                                }
                            }
                            catch { }
                        }
                        else if (data.ContentType == "application/x-www-form-urlencoded" && data.DataLength < 1024*1024*100)
                        {
                            // Parse as normal=query&string=parameters
                            using (var sr = new StreamReader(data.DataStream, Encoding.UTF8, leaveOpen: true))
                                GetParametersFromQueryString(rawParameters, await sr.ReadToEndAsync());
                        }
                        else if (data.ContentType != null && data.ContentType.StartsWith("multipart/form-data; boundary=")) // Usually a file upload, but can be form data as well
                        {
                            data.FileData = new List<HttpClientFileData>();
                            var boundary = data.ContentType.Substring(30);
                            if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                                boundary = boundary.Trim('"');
                            var boundaryBytes = ASCIIEncoding.ASCII.GetBytes("--"+boundary);
                            long curDataPos = 0;
                            long startContent = -1;

                            // TODO: Optimize

                            for (; curDataPos < data.DataLength - boundaryBytes.Length; curDataPos++)
                            {
                                data.DataStream.Position = curDataPos;
                                    
                                /// Find the boundary 
                                var found = true;
                                for (var j = 0; j < boundaryBytes.Length; j++)
                                {
                                    var curChar = (byte)data.DataStream.ReadByte();
                                    if (curChar != boundaryBytes[j])
                                    {
                                        found = false;
                                        break;
                                    }
                                }
                                
                                if (found)
                                {
                                    if (startContent < 0)
                                    {
                                        // Found the first boundary
                                        startContent = curDataPos + boundaryBytes.Length + 2;  // The start boundary is followed by \r\n
                                        continue;
                                    }

                                    // Found the last boundary
                                    long endContent = curDataPos - 2;

                                    /// Handle content                                    
                                    // Parse headers
                                    var headers = new Dictionary<string, string>();
                                    var headerName = "";
                                    var curLineStart = startContent;
                                    var curValueStart = startContent;

                                    data.DataStream.Position = startContent;
                                    var last3Bytes = new byte[3];
                                    var curData = "";
                                    for (var i = startContent; i < endContent; i++)
                                    {
                                        last3Bytes[0] = last3Bytes[1];
                                        last3Bytes[1] = last3Bytes[2];
                                        last3Bytes[2] = (byte)data.DataStream.ReadByte();                                        

                                        if (i >= 2 && last3Bytes[2] == '\n' && last3Bytes[0] == '\n')
                                        {
                                            // Found the end of the header
                                            startContent = i + 1;
                                            break;
                                        }
                                        else if (headerName == "" && last3Bytes[2] == ':')
                                        {
                                            // Got a header name
                                            headerName = curData;
                                            curData = "";
                                            curValueStart = i + 2;
                                            i++; // We can ignore the next character (space)
                                            data.DataStream.ReadByte();
                                        }
                                        else if (last3Bytes[2] == '\r' && headerName != "")
                                        {
                                            // Got a header value

                                            var headerValue = curData;
                                            curData = "";
                                            if (!headers.ContainsKey(headerName.ToLower()))
                                                headers.Add(headerName.ToLower(), headerValue);
                                            headerName = "";
                                            curLineStart = i + 2;

                                            //i++; // We can ignore the next character (\n)
                                            //data.DataStream.ReadByte();
                                        }
                                        else if (last3Bytes[2] != '\r' && last3Bytes[2] != '\n')
                                        {
                                            curData += Convert.ToChar(last3Bytes[2]);
                                        }
                                    }

                                    if (headers.Count > 0)
                                    {
                                        var skipFileData = false;
                                        string? fileName = null;
                                        // See if this is a form element, or a file upload
                                        if (headers.ContainsKey("content-disposition"))
                                        {
                                            var headerValue = headers["content-disposition"];
                                            var formFieldName = Between(headerValue, "name=\"", "\"");
                                            if (formFieldName != null)
                                            {
                                                if (headerValue.Contains("filename="))
                                                {
                                                    // File upload
                                                    var headerValueFileName = headerValue.Substring(headerValue.IndexOf("filename=") + 9);
                                                    if (headerValueFileName.Contains(";"))
                                                        headerValueFileName = headerValueFileName.Substring(0, headerValueFileName.IndexOf(";"));
                                                    fileName = headerValueFileName.Replace("\"", "").Trim();
                                                }
                                                else
                                                {
                                                    // Form data                                                    
                                                    data.DataStream.Position = startContent;
                                                    var dataBlock = new byte[endContent - startContent];
                                                    var dataLen = 0;
                                                    while (dataLen < dataBlock.Length)
                                                    {
                                                        var tmpDataLen = data.DataStream.Read(dataBlock);
                                                        if (tmpDataLen <= 0)
                                                            throw new Exception("Invalid posted data");
                                                        dataLen += tmpDataLen;
                                                    }
                                                    
                                                    rawParameters.Add(formFieldName, ASCIIEncoding.UTF8.GetString(dataBlock));                                                    
                                                    skipFileData = true;
                                                }
                                            }
                                        }
                                        
                                        if (!skipFileData)
                                        {
                                            data.FileData.Add(new HttpClientFileData(fileName, startContent, endContent - startContent, headers, data.DataStream));
                                        }
                                    }

                                    // Prepare for next content
                                    startContent = endContent + 2 + boundaryBytes.Length + 2; // Next content is after \r\n, the end boundary, and another \r\n
                                }
                            }
                        }
                        
                        data.DataStream.Position = 0;
                    }

                    var paramValues = new List<object?>();
                    foreach (var param in route.ApplicationMethodParameters)
                    {
                        if (param.ParameterType == typeof(HttpClientData))
                            paramValues.Add(data);
                        else if (param.Name != null && rawParameters.ContainsKey(param.Name))
                        {
                            if (param.ParameterType == typeof(string))
                                paramValues.Add(rawParameters[param.Name]);
                            else if (param.ParameterType == typeof(int))
                                paramValues.Add(Int32.Parse(rawParameters[param.Name]));
                            else if (param.ParameterType == typeof(bool))
                                paramValues.Add(Boolean.Parse(rawParameters[param.Name]));
                            else if (param.ParameterType == typeof(double))
                                paramValues.Add(Double.Parse(rawParameters[param.Name], CultureInfo.InvariantCulture));
                            else if (param.ParameterType == typeof(float))
                                paramValues.Add((float)(Double.Parse(rawParameters[param.Name], CultureInfo.InvariantCulture)));
                            else if (rawParameters[param.Name].Length > 0 && (rawParameters[param.Name].StartsWith("{") || rawParameters[param.Name].StartsWith("[")))
                            {
                                // Complex object posted in JSON, try to deserialize
                                try
                                {
                                    var obj = JsonSerializer.Deserialize(rawParameters[param.Name], param.ParameterType);
                                    paramValues.Add(obj);
                                }
                                catch
                                {
                                    paramValues.Add(null); // Error while deserializing 
                                }
                            }
                            else
                                paramValues.Add(null); // Unsupported
                        }
                        else if (param.HasDefaultValue)
                            paramValues.Add(param.DefaultValue);
                        else
                            paramValues.Add(null);
                    }
                    var responseObj = route.ApplicationMethod.Invoke(route.Application, paramValues.ToArray());
                    if (responseObj is Task)
                    {
                        await Task.WhenAll((Task)responseObj);
                        if (responseObj is Task<HttpResponse>)
                            responseObj = ((Task<HttpResponse>)responseObj).Result;
                        else if (responseObj is Task<HttpResponse?>)
                            responseObj = ((Task<HttpResponse?>)responseObj).Result;
                        else
                        {
                            var resultProperty = ((Task)responseObj).GetType().GetProperty("Result"); // TODO: See if we can skip this reflection step for better performance
                            if (resultProperty != null)
                                responseObj = resultProperty.GetValue(responseObj);
                            else
                                responseObj = null;
                        }
                    }                    
                    
                    if (responseObj is HttpResponse)
                    {
                        response = (HttpResponse?)responseObj;
                    }
                    else if (responseObj != null)
                    {
                        response = new HttpResponse()
                        {
                            ResponseObject = responseObj
                        };
                    }                    
                }
                else if (route.Proxy != null)
                {
                    // Just connect to http endpoint and stream all content
                    try
                    {
                        var tmpRoutePrefix = routeKey.Substring(routeKey.IndexOf("/"));
                        if (tmpRoutePrefix.EndsWith("/*"))
                            tmpRoutePrefix = tmpRoutePrefix.Substring(0, tmpRoutePrefix.Length - 2);

                        // Build request
                        StringBuilder request = new StringBuilder();
                        request.AppendLine(data.Method + " /" + data.RequestRaw.Substring(tmpRoutePrefix.Length).Replace("*", "") + " HTTP/1.1"); // TODO: Remove any folder in our routing path
                        request.AppendLine("Host: " + new Uri(route.Proxy).Host);
                        if (route.ShouldSendForwardHeaders)
                        {
                            if (data.Headers.ContainsKey("host"))
                                request.AppendLine("X-Forwarded-Host: " + data.Headers["host"]);
                            request.AppendLine("X-Forwarded-Proto: " + (data.Client.StreamIsEncrypted ? "https" : "http"));
                            request.AppendLine("X-Forwarded-For: " + data.Client.RemoteAddress);
                        }

                        foreach (var requestHeader in data.FullRawHeaders.ToList())
                        {
                            if (requestHeader.Key == "Host")
                                continue;
                            if (route.ProxyInterceptHandler != null && route.ProxyShouldInterceptHandler != null && requestHeader.Key == "Accept-Encoding")
                                continue;
                            request.AppendLine(requestHeader.Key + ": " + requestHeader.Value);
                        }
                        request.AppendLine();                        

                        using (var responseStream = await SharedTcpClient.ExecuteHttpRequest(route.Proxy, request.ToString(), data.DataStream))
                        {
                            var intercept = route.ProxyShouldInterceptHandler != null && route.ProxyInterceptHandler != null && route.ProxyShouldInterceptHandler(data.Request, responseStream.ResponseHeaders);

                            if (intercept)
                            {                                
                                var content = new StreamReader(responseStream, Encoding.UTF8).ReadToEnd();
                                var newContent = route.ProxyInterceptHandler!(data.Request, responseStream.ResponseHeaders, content);
                                var newResponseHeaders = string.Join("\r\n", responseStream.ResponseHeaders
                                    .Split(new String[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(a=> !a.StartsWith("Content-Length:") && !a.StartsWith("Transfer-Encoding:")));
                                
                                await client.SendString(newResponseHeaders+ "\r\nContent-Length: " + newContent.Length + "\r\n\r\n");
                                await client.SendString(newContent, Encoding.UTF8);
                            }
                            else
                            {
                                responseStream.IncludeChunkedHeadersInResponse = true;
                                await client.SendString(responseStream.ResponseHeaders);
                                await client.SendStream(responseStream, 0, -1);
                            }
                        }
                        return;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message + "\r\n" + e.StackTrace);
                        response = new HttpResponse()
                        {
                            StatusCode = 500,
                            ContentType = "text/plain",
                            Data = ASCIIEncoding.UTF8.GetBytes("Proxy error: " + e.Message + "\r\n" + e.StackTrace)
                        };
                    }
                }
                else if (route.CustomHandler != null) // Custom code
                {
                    try
                    { 
                        await route.CustomHandler(client, data);
                        return;
                    }
                    catch (Exception e)
                    {
                        response = new HttpResponse()
                        {
                            StatusCode = 500,
                            ContentType = "text/plain",
                            Data = ASCIIEncoding.UTF8.GetBytes("Remote instance error: " + e.Message)
                        };
                    }
                }
                else if (route.WebsocketReceiveHandler != null)
                {
                    // Websocket route, see if the request is actually meant for a websocket 
                    if (data.Headers.ContainsKey("sec-websocket-key"))
                    {
                        // Send a special response
                        var accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(data.Headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        response = new HttpResponse()
                        {
                            StatusCode = 101,
                            Headers = new Dictionary<string, string>()
                            {
                                { "Upgrade", "websocket" },
                                { "Connection", "Upgrade" },
                                { "Sec-WebSocket-Accept", accept }
                            },
                            ContentType = ""
                        };
                        data.WebsocketFrameHandler = route.WebsocketReceiveHandler;
                        data.WebsocketDisconnectedHandler = route.WebsocketDisconnectHandler;
                    }
                    else
                    {
                        response = new HttpResponse()
                        {
                            StatusCode = 400,
                            ContentType = "text/plain",
                            Data = ASCIIEncoding.UTF8.GetBytes("Invalid websocket request")
                        };
                    }

                }
            } 

            if (response == null)
            {
                response = new HttpResponse()
                {
                    StatusCode = 404,
                    ContentType = "text/plain",
                    Data = ASCIIEncoding.UTF8.GetBytes("Not found")
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
                    response.ContentLengthStream = (endByte - startByte) + 1;
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
                response.ContentType = (route?.ContentType ?? ContentTypeUtil.GetContentTypeFromFileName(response.FileName));

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

            if (PostProcessors != null)
            {
                foreach (var processor in PostProcessors)
                    processor(data, response);
            }

            var codeText = ((System.Net.HttpStatusCode)response.StatusCode).ToString();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("HTTP/1.1 " + response.StatusCode + " " + codeText);
            if (response.ContentType != "")
                sb.AppendLine("Content-Type: " + (response.ContentType ?? "text/html"));


            // Check if we can actually return the response with gzip compression
            if (response.GZipResponse && data.Headers.ContainsKey("accept-encoding") && data.Headers["accept-encoding"].Contains("gzip") && response.Data == null && response.Stream != null)
            {
                sb.AppendLine("Content-Encoding: gzip");
                response.ChunkedResponse = true; // required as we don't know the final transfer content length yet
            }
            else
            {

                response.GZipResponse = false;
            }

            if ((response.ChunkedResponse || response.ContentLengthStream < 0) && response.Data == null && response.Stream != null) {
                sb.AppendLine("Transfer-Encoding: chunked");
                response.Stream = new ChunkedStream(response.Stream, response.GZipResponse);
                response.ContentLengthStream = -1;
            }
            else
            {
                if (response.ContentType != "")
                    sb.AppendLine("Content-Length: " + (response.Data != null ? response.Data.Length : response.Stream != null ? response.ContentLengthStream : 0));
            }            

            if (response.Headers != null)
            {
                foreach (var header in response.Headers)
                    sb.AppendLine(header.Key + ": " + header.Value);
            }

            sb.AppendLine();

            var tmpResponseHeader = ASCIIEncoding.ASCII.GetBytes(sb.ToString());

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


            if (data.WebsocketFrameHandler != null && route != null && route.WebsocketConnectHandler != null)
            {
                // This request is upgraded to a websocket connection, execute callback handler
                await route.WebsocketConnectHandler(data);
            }
        }
        private string? Between(string txt, string start, string end)
        {
            var pos = txt.IndexOf(start);
            if (pos < 0)
                return null;
            txt = txt.Substring(pos + start.Length);
            pos = txt.IndexOf(end);
            if (pos < 0)
                return null;
            return txt.Substring(0, pos);
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

        public void AddFileRoute(string domain, string path, string localPath, string? contentType)
        {            
            AddRoute(domain, path, new Route()
            {
                ContentType = contentType,
                LocalPath = localPath
            });
        }
        public void AddProxyRoute(string domain, string path, string targetUrl, Func<string, string, bool>? shouldInterceptHandler = null, Func<string, string, string, string>? interceptHandler=null, bool shouldSendForwardHeaders=true)
        {
            AddRoute(domain, path, new Route()
            {
                Proxy = targetUrl,
                ProxyInterceptHandler = interceptHandler,
                ProxyShouldInterceptHandler = shouldInterceptHandler,
                ShouldSendForwardHeaders = shouldSendForwardHeaders
            });
        }
        public void AddContentRoute(string domain, string path, byte[] contents, string contentType)
        {
            AddRoute(domain, path, new Route()
            {
                ContentType = contentType,
                Contents = contents
            });
        }
        public void AddApplicationRoute(string domain, string path, object httpApplication, bool lowerCaseMethods=true, bool allPublicMethods=false)
        {
            // Add all methods of the given httpApplication class as seperate routes. The 'Other' method will be used if no suitable methods are found for a request.
            var publicMethods = httpApplication.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var ignoreMethods = typeof(object).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(a => a.Name).ToArray();
            foreach (var method in publicMethods)
            {
                if (allPublicMethods && (ignoreMethods.Contains(method.Name) || method.ReturnType == typeof(void)))
                    continue;
                if (!allPublicMethods && method.ReturnType != typeof(HttpResponse) && method.ReturnType != typeof(Task<HttpResponse>) && method.ReturnType != typeof(Task<HttpResponse?>))
                    continue;
                AddRoute(domain, path + (method.Name == "Index" ? "" : method.Name == "Other" ? "/*" : "/"+(lowerCaseMethods ? method.Name.ToLower() : method.Name)), new Route()
                {
                    Application = httpApplication,
                    ApplicationMethod = method,
                    ApplicationMethodParameters = method.GetParameters() 
                });
            }            
        }
        public void AddWebsocketRoute(string domain, string path, Func<HttpClientData, byte, byte[], ulong, ulong, Task> messageReceivedHandler, Func<HttpClientData, Task>? connectHandler=null, Func<HttpClientData, Task>? disconnectHandler = null)
        {
            AddRoute(domain, path, new Route()
            {
                WebsocketConnectHandler = connectHandler,
                WebsocketReceiveHandler = messageReceivedHandler,
                WebsocketDisconnectHandler = disconnectHandler
            });
        }
        internal void AddCustomRoute(string domain, string path, Func<Client, HttpClientData, Task> handleCallback)
        {            
            AddRoute(domain, path, new Route()
            {
                CustomHandler = handleCallback
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
            public string? ContentType { get; set; }
            public byte[]? Contents { get; set; }
            public string? LocalPath { get; set; }
            public object? Application { get; set; }
            public string? Proxy { get; set; }
            public bool ShouldSendForwardHeaders { get; set; }
            public Func<string, string, bool>? ProxyShouldInterceptHandler { get; set; } // bool ShouldIntercept(requestPath, responseHeaders)
            public Func<string, string, string, string>? ProxyInterceptHandler { get; set; } // string NewContent(requestPath, responseHeaders, responseContent)
            public MethodInfo? ApplicationMethod { get; set; }
            public ParameterInfo[]? ApplicationMethodParameters { get; set; }
            public Func<Client, HttpClientData, Task>? CustomHandler { get; set; }            
            public Func<HttpClientData, Task>? WebsocketConnectHandler { get; set; }
            public Func<HttpClientData, byte, byte[], ulong, ulong, Task>? WebsocketReceiveHandler { get; set; }
            public Func<HttpClientData, Task>? WebsocketDisconnectHandler { get; set; }
        }

        public class HttpClientData
        {
            public required Client Client { get; set; }
            public required byte[] IncomingBuffer { get; set; }
            public int IncomingBufferLength { get; set; }
            public string? Method { get; set; }
            public string? RequestRaw { get; set; } // Full undecoded request ( starts with / ) 
            public string? Request { get; set; } // Full decoded request ( starts with / )
            public string? RequestPage { get; set; } // Request without the query string parameters
            public string? RequestPageShort { get; set; } // RequestPage without the route prefix
            public string Host { get; set; } = "";
            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
            public List<KeyValuePair<string, string>> FullRawHeaders { get; set; } = new List<KeyValuePair<string, string>>(); // Includes duplicates of header keys
            public long ContentLength { get; set; }
            public string? ContentType { get; set; }
            internal byte[]? Data { get; set; }
            public Stream? DataStream { get; set; }
            public string? DataTempFileName { get; set; }
            public long DataLength { get; set; }
            public List<HttpClientFileData>? FileData { get; set; }
            public Func<HttpClientData, byte, byte[], ulong, ulong, Task>? WebsocketFrameHandler { get; set; }
            public Func<HttpClientData, Task>? WebsocketDisconnectedHandler { get; set; }
            public string? CookieValue(string cookieName)
            {
                foreach (var header in FullRawHeaders)
                {
                    if (header.Key.ToLower() != "cookie")
                        continue;
                                        
                    var cookies = header.Value.Split(';');
                    foreach (var cookie in cookies)
                    {
                        var singleCookie = cookie.Trim().Split('=',2);
                        if (singleCookie[0] == cookieName && singleCookie.Length == 2)
                            return singleCookie[1];
                    }                    
                }
                return null;
            }

            public async Task SendWebsocketMessage(byte opcode, byte[] data, ulong offset, ulong len)
            {
                if (WebsocketFrameHandler == null)
                    return;
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
                    header[9] = (byte)(len);
                }

                header[0] = opcode;
                header[0] |= 0b10000000; // set fin flag

                await Client.SendData(header, 0, header.Length, false);
                await Client.SendData(data, (int)offset, (int)len, true);
            }
            public async Task SendWebsocketMessage(byte opcode, byte[] data)
            {
                await SendWebsocketMessage(opcode, data, 0, (ulong)data.Length);
            }
            public async Task SendWebsocketText(string text)
            {
                await SendWebsocketMessage(0x01, ASCIIEncoding.UTF8.GetBytes(text));
            }
            public async Task SendWebsocketJsonObject(object obj)
            {
                await SendWebsocketMessage(0x01, ASCIIEncoding.UTF8.GetBytes(JsonSerializer.Serialize(obj)));
            }
        }        

        public class HttpClientFileData
        {
            public HttpClientFileData(string? fileName, long dataFrom, long dataLength, Dictionary<string, string> headers, Stream dataStream)
            {
                Headers = headers;
                FileName = fileName;
                DataFrom = dataFrom;
                DataLength = dataLength;
                DataStream = dataStream;
            }
            public Dictionary<string, string> Headers { get; set; }
            public string? FileName { get; set; }
            public long DataFrom { get; set; }
            public long DataLength { get; set; }
            internal Stream DataStream { get; set; }
            public Stream GetStream()
            {
                // TODO: Also add support for Content-Encoding: base64
                return new SubStream(DataStream, DataFrom, DataLength);
            }
        }
        public class HttpResponse
        {
            public int StatusCode { get; set; } = 200;
            public string? ContentType { get; set; }
            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

            // File, Data or Stream or object (json)
            public string? FileName { get; set; }
            public byte[]? Data { get; set; }
            public Stream? Stream { get; set; }
            public long ContentLengthStream { get; set; } = -1;
            public long ContentOffsetStream { get; set; }
            public object? ResponseObject { get; set; }
            public bool ChunkedResponse { get; set; }
            public bool GZipResponse { get; set; }
            public HttpResponse()
            {

            }
            public HttpResponse(int statusCode, object responseObjectCode)
            {
                StatusCode = statusCode;
                ResponseObject = responseObjectCode;
            }
        }
        
        
    }
}
