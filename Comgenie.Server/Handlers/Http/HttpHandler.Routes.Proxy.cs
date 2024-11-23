using Comgenie.Server.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                HandleExecuteRequestAsync = async (client, data) =>
                {
                    if (data.Request == null)
                        return null;
                    // Just connect to http endpoint and stream all content
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

                        request.AppendLine(data.Method + " " + requestRaw + " HTTP/1.1"); // TODO: Remove any folder in our routing path
                        request.AppendLine("Host: " + new Uri(targetUrl).Host);
                        if (shouldSendForwardHeaders)
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
                            if (interceptHandler != null && shouldInterceptHandler != null && requestHeader.Key == "Accept-Encoding")
                                continue;
                            request.AppendLine(requestHeader.Key + ": " + requestHeader.Value);
                        }
                        request.AppendLine();

                        using (var responseStream = await SharedTcpClient.ExecuteHttpRequest(targetUrl, request.ToString(), data.DataStream))
                        {
                            var intercept = shouldInterceptHandler != null && interceptHandler != null && shouldInterceptHandler((data.Request, responseStream.ResponseHeaders));
                            if (intercept)
                            {
                                var content = new StreamReader(responseStream, Encoding.UTF8).ReadToEnd();
                                var newContent = interceptHandler!((data.Request, responseStream.ResponseHeaders, content));
                                var newResponseHeaders = string.Join("\r\n", responseStream.ResponseHeaders
                                    .Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(a => !a.StartsWith("Content-Length:") && !a.StartsWith("Transfer-Encoding:")));

                                await client.SendString(newResponseHeaders + "\r\nContent-Length: " + newContent.Length + "\r\n\r\n");
                                await client.SendString(newContent, Encoding.UTF8);
                            }
                            else
                            {
                                responseStream.IncludeChunkedHeadersInResponse = true;
                                await client.SendString(responseStream.ResponseHeaders);
                                await client.SendStream(responseStream, 0, -1);
                            }
                        }
                        return new HttpResponse()
                        {
                            ResponseFinished = true,
                        };
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message + "\r\n" + e.StackTrace);
                        return new HttpResponse()
                        {
                            StatusCode = 500,
                            ContentType = "text/plain",
                            Data = Encoding.UTF8.GetBytes("Proxy error: " + e.Message + "\r\n" + e.StackTrace)
                        };
                    }
                }
            });
        }
    }
}
