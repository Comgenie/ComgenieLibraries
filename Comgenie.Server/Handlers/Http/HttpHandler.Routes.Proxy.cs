using Comgenie.Server.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.Http.HttpHandler;

namespace Comgenie.Server.Handlers.Http
{
    public partial class HttpHandler
    {
        /// <summary>
        /// Add a proxy route to an external web address. This can be a specific path or any path using a wildcard. 
        /// Optionally, the response can be intercepted and modified, note that this does add a performance hit, so use the 'shouldInterceptHandler' to filter requests to only intercept the ones which are really needed.
        /// </summary>
        /// <param name="domain">Add route on this httphandler domain</param>
        /// <param name="path">Specific or wildcard path like /file.txt or /* </param>
        /// <param name="targetUrl">Web address including https:// , when using wildcards, everything in the wildcard will be added to the end of this address.</param>
        /// <param name="shouldInterceptHandler">Callback to indicate if you want to intercept this request response.</param>
        /// <param name="interceptHandler">Actual intercept handler, should return the modified contents to return to the client.</param>
        /// <param name="shouldSendForwardHeaders">Include X-Forwarded-* headers to the external host.</param>
        public void AddProxyRoute(string domain, string path, string targetUrl, Func<(string requestPath, string responseHeaders), bool>? shouldInterceptHandler = null, Func<(string requestPath, string responseHeaders, string responseContent), string>? interceptHandler = null, bool shouldSendForwardHeaders = true)
        {
            if (!path.StartsWith("/"))
                path = "/" + path;
            var pathWithoutWildcard = path.Replace("*", "");

            AddRoute(domain, path, new Route()
            {
                HandleExecuteRequestAsync = async (client, data, cancellationToken) =>
                {
                    if (data.Request == null)
                        return null;

                    // Just connect to http endpoint and stream all content
                    // We'll do 2 attempts as the sharedtcpclient might break in some non-complaint http servers
                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            // Build request
                            StringBuilder request = new StringBuilder();
                            var requestRaw = data.RequestRaw ?? "";
                            if (!requestRaw.StartsWith("/"))
                                requestRaw = "/" + requestRaw;

                            // route: /* ,  path: / , result: /
                            // route: /test/*,  path: /test/ ,   result: /
                            // route: /test.html, path: /test.html, result: /
                            // route: /test/*, path: /test/blah, result: /blah

                            requestRaw = "/" + requestRaw.Substring(pathWithoutWildcard.Length);

                            request.Append($"{data.Method} {requestRaw} HTTP/1.1\r\n"); // TODO: Remove any folder in our routing path
                            request.Append($"Host: {new Uri(targetUrl).Host}\r\n");
                            if (shouldSendForwardHeaders)
                            {
                                if (data.Headers.ContainsKey("host"))
                                    request.Append($"X-Forwarded-Host: {data.Headers["host"]}\r\n");
                                request.Append($"X-Forwarded-Proto: {(data.Client.StreamIsEncrypted ? "https" : "http")}\r\n");
                                request.Append($"X-Forwarded-For: {data.Client.RemoteAddress}\r\n");
                            }

                            foreach (var requestHeader in data.FullRawHeaders.ToList())
                            {
                                if (requestHeader.Key == "Host")
                                    continue;
                                if (interceptHandler != null && shouldInterceptHandler != null && requestHeader.Key == "Accept-Encoding")
                                    continue;
                                request.Append($"{requestHeader.Key}: {requestHeader.Value}\r\n");
                            }
                            request.Append("\r\n");

                            
                            using (var responseStream = await SharedTcpClient.ExecuteHttpRequest(targetUrl, request.ToString(), data.DataStream, cancellationToken))
                            {
                                var intercept = shouldInterceptHandler != null && interceptHandler != null && shouldInterceptHandler((data.Request, responseStream.ResponseHeaders));
                                if (intercept)
                                {
                                    var content = await new StreamReader(responseStream, Encoding.UTF8).ReadToEndAsync(cancellationToken);
                                    var newContent = interceptHandler!((data.Request, responseStream.ResponseHeaders, content));
                                    var newResponseHeaders = string.Join("\r\n", responseStream.ResponseHeaders
                                        .Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                                        .Where(a => !a.StartsWith("Content-Length:") && !a.StartsWith("Transfer-Encoding:")));

                                    await client.SendStringAsync(newResponseHeaders + "\r\nContent-Length: " + newContent.Length + "\r\n\r\n", cancellationToken: cancellationToken);
                                    await client.SendStringAsync(newContent, Encoding.UTF8, cancellationToken: cancellationToken);
                                }
                                else
                                {
                                    
                                    responseStream.IncludeChunkedHeadersInResponse = true;
                                    await client.SendStringAsync(responseStream.ResponseHeaders, cancellationToken: cancellationToken);
                                    await client.SendStreamAsync(responseStream, 0, -1, cancellationToken: cancellationToken);
                                }

                                // TODO: For logging purposes also fill in some of the remaining HttpResponse fields based on the responseStream.ResponseHeaders
                                var space = responseStream.ResponseHeaders.IndexOf(' ');
                                var responseCode = 0;
                                if (space > 0)
                                {
                                    var secondSpace = responseStream.ResponseHeaders.IndexOf(' ', space + 1);
                                    if (secondSpace > 0)
                                        int.TryParse(responseStream.ResponseHeaders.Substring(space + 1, secondSpace - (space + 1)), out responseCode);
                                }

                                return new HttpResponse()
                                {
                                    StatusCode = responseCode,
                                    ResponseFinished = true,
                                };
                            }

                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("[Proxy error] " + e.Message + "\r\n" + e.StackTrace);

                            if (attempt < 2)
                                continue;

                            return new HttpResponse()
                            {
                                StatusCode = 500,
                                ContentType = "text/plain",
                                Data = Encoding.UTF8.GetBytes("Proxy error")
                            };
                        }
                    }

                    return new HttpResponse()
                    {
                        StatusCode = 500,
                        ContentType = "text/plain",
                        Data = Encoding.UTF8.GetBytes("No response")
                    };
                }
            });
        }
    }
}
