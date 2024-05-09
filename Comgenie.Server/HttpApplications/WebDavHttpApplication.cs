using Comgenie.Server.Handlers;
using Comgenie.Server.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using static Comgenie.Server.Handlers.HttpHandler;

namespace Comgenie.Server.HttpApplications
{
    public abstract class WebDavHttpApplication
    {
        public HttpHandler.HttpResponse Index(HttpHandler.HttpClientData httpClientData)
        {
            return Other(httpClientData);
        }
        public HttpHandler.HttpResponse Other(HttpHandler.HttpClientData httpClientData)
        {
            Console.WriteLine(httpClientData.Method + " " + httpClientData.RequestRaw);
            Console.WriteLine("Short url: " + httpClientData.RequestPageShort);
            foreach (var h in httpClientData.FullRawHeaders)
            {
               
                Console.WriteLine("HEADER " + h.Key + ": " + (h.Key == "Authorization" ? "XXX" : h.Value));
            }

            string? username = null;
            string? password = null;
            if (httpClientData.Headers.ContainsKey("authorization"))
            {
                var parts = httpClientData.Headers["authorization"].Split(' ', 2);
                if (parts.Length >= 2 && parts[0] == "Basic")
                {
                    var raw = ASCIIEncoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                    var userParts = raw.Split(':', 2);
                    if (userParts.Length >= 2)
                    {
                        username = userParts[0];
                        password = userParts[1];
                    }
                }
            }
            object authObject = CheckAuthorization(httpClientData, username, password);

            if (authObject == null)
            {
                return new HttpHandler.HttpResponse()
                {
                    StatusCode = 401,
                    Headers = new Dictionary<string, string>()
                    {
                        { "WWW-Authenticate", "Basic realm=\"You shall not pass\", charset=\"UTF-8\"" }
                    }
                };
            }

            if (httpClientData.Method == "OPTIONS")
            {
                return new HttpHandler.HttpResponse()
                {
                    StatusCode = 200,
                    ContentType = "text/plain",
                    Data = new byte[] { },
                    Headers = new Dictionary<string, string>()
                    {
                        { "Allow", "OPTIONS, GET, POST, PUT, DELETE, MKCOL, MOVE, COPY, HEAD" }
                    }
                };
            }

            if (httpClientData.Method == "DELETE")
            {
                // Delete file
                var result = DeleteFile(authObject, httpClientData.RequestPageShort);

                if (!result)
                    return new HttpHandler.HttpResponse(404, new { Error = "Item not found" });
                return new HttpHandler.HttpResponse(204, new { Success = true });                
            }

            if (httpClientData.Method == "GET" || httpClientData.Method == "HEAD")
            {
                // Download file
                var file = GetFile(authObject, httpClientData.RequestPageShort);
                if (file == null)
                    return new HttpHandler.HttpResponse(404, new { Error = "Item not found" });

                if (!string.IsNullOrEmpty(file.LocalFileName))
                {
                    return new HttpHandler.HttpResponse()
                    {
                        StatusCode = 200,
                        FileName = file.LocalFileName
                    };
                }
                else
                {
                    return new HttpHandler.HttpResponse()
                    {
                        StatusCode = 200,
                        ContentLengthStream = file.FileSize,
                        ContentType = file.ContentType,
                        Stream = file.Stream
                    };
                }
            }

            if (httpClientData.Method == "PROPPATCH")
            {
                // TODO, Handle correctly but this makes sure windows doesn't complain
                return new HttpHandler.HttpResponse()
                {
                    StatusCode = 200,
                    ContentType = "text/xml; charset=\"utf-8\"",
                    Data = ASCIIEncoding.UTF8.GetBytes("<?xml version=\"1.0\"?><a:multistatus xmlns:a=\"DAV:\"><a:response><a:propstat><a:status>HTTP/1.1 200 OK</a:status></a:propstat></a:response></a:multistatus>")
                };
            }

            if (httpClientData.Method == "PUT")
            {
                // Upload file
                DateTime dateModified = DateTime.UtcNow;
                if (httpClientData.Headers.ContainsKey("x-oc-mtime"))
                {
                    var epoch = long.Parse(httpClientData.Headers["x-oc-mtime"]);
                    dateModified = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                }

                if (httpClientData.DataStream == null)
                    httpClientData.DataStream = new MemoryStream(); // empty

                httpClientData.DataStream.Position = 0;
                var result = PutFile(authObject, httpClientData.RequestPageShort, httpClientData.DataStream, dateModified);
                if (!result)
                    return new HttpHandler.HttpResponse(409, new { Error = "Could not save file" });
                return new HttpHandler.HttpResponse(200, new { Success = true });
            }

            /*if (httpClientData.Method == "LOCK")
            {
                // TODO
                return new HttpHandler.HttpResponse(200, new { Success = true });
            }*/

            if (httpClientData.Method == "MOVE")
            {
                // Rename/move file
                if (!httpClientData.Headers.ContainsKey("destination"))
                    return new HttpHandler.HttpResponse(500, new { Error = "Missing destination" });

                var newPath = GetWebDavFilePath(httpClientData, httpClientData.Headers["destination"]);                

                var result = MoveFile(authObject, httpClientData.RequestPageShort, newPath);
                if (!result)
                    return new HttpHandler.HttpResponse(409, new { Error = "Could not move file" });
                return new HttpHandler.HttpResponse(201, new { Success = true });
            }

            if (httpClientData.Method == "COPY")
            {
                // Copy file
                if (!httpClientData.Headers.ContainsKey("destination"))
                    return new HttpHandler.HttpResponse(500, new { Error = "Missing destination" });
                
                var newPath = GetWebDavFilePath(httpClientData, httpClientData.Headers["destination"]);

                var result = CopyFile(authObject, httpClientData.RequestPageShort, newPath);
                if (!result)
                    return new HttpHandler.HttpResponse(409, new { Error = "Could not move file" });
                return new HttpHandler.HttpResponse(201, new { Success = true });
            }

            if (httpClientData.Method == "MKCOL")
            {
                // Make folder/collection
                var folderPath = httpClientData.RequestPageShort;
                if (folderPath.EndsWith("/"))
                    folderPath = folderPath.Substring(0, folderPath.Length - 1);
                var result = MakeCollection(authObject, folderPath);
                if (!result)
                    return new HttpHandler.HttpResponse(409, new { Error = "Could not create folder" });
                return new HttpHandler.HttpResponse(201, new { Success = true });
            }

            if (httpClientData.Method == "PROPFIND")
            {
                StringBuilder sb = new StringBuilder();
                List<WebDavFileInfo> files = new List<WebDavFileInfo>();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");

                var path = httpClientData.RequestPageShort; // WhatsAppImages/IMG-20140419-WA0001.jpg
                if (path.EndsWith("/"))
                    path = path.Substring(0, path.Length - 1); // WhatsAppImages/IMG-20140419-WA0001.jpg
                var fileOrFolderName = path.Contains("/") ? path.Substring(path.LastIndexOf("/") + 1) : path; // IMG-20140419-WA0001.jpg
                var parentFolder = path.Contains("/") ? path.Substring(0, path.LastIndexOf("/")) : ""; // WhatsAppImages

                // Retrieve the parent folder to see if the item requested is a folder or file
                Console.WriteLine("Trying to find " + fileOrFolderName + " in " + parentFolder);
                var requestedItem = path != "" ? 
                    GetFileInfo(authObject, path) :
                    new WebDavFileInfo()
                    {
                        IsCollection = true,
                        Name = "",
                    };
                Console.WriteLine("Requested item: " + requestedItem);
                var thisFolder = requestedItem != null && requestedItem.IsCollection ? path : parentFolder;

                if (requestedItem == null)
                {
                    var allFiles = ListFiles(authObject, parentFolder).ToList();
                    Console.WriteLine("Not found, total items: " + allFiles.Count);
                    var exi = allFiles.FirstOrDefault(a => a.Name.ToLower().Trim().EndsWith(fileOrFolderName.ToLower().Trim()));
                    if (exi != null)
                        Console.WriteLine("Found with similar name: " + exi.Name);
                    // Not found
                    sb.AppendLine("<propfind xmlns=\"DAV:\">");
                    sb.AppendLine("<href>" + HttpUtility.HtmlEncode(GetApplicationRootUrl(httpClientData, true)) + "</href>");
                    sb.AppendLine("<propstat>");
                    sb.AppendLine("<prop></prop>");
                    sb.AppendLine("<status>HTTP/1.1 404 NOT FOUND</status>");
                    sb.AppendLine("</propstat>");
                    sb.AppendLine("</propfind>");
                    return new HttpHandler.HttpResponse()
                    {
                        StatusCode = 404,
                        ContentType = "text/xml; charset=\"utf-8\"",
                        Data = ASCIIEncoding.UTF8.GetBytes(sb.ToString()),
                    };
                }

                // Always return information about the current file or container (windows requires that)
                files.Add(requestedItem);
                
                // List files in folder/collection
                var depth = httpClientData.Headers.ContainsKey("depth") ? Int32.Parse(httpClientData.Headers["depth"]) : 999;
                var skipItems = 0;
                for (var i = 0; i < depth && requestedItem.IsCollection; i++)
                {
                    skipItems = files.Count;
                    var tmpFiles = new List<WebDavFileInfo>();

                    if (i == 0)
                    {
                        tmpFiles = ListFiles(authObject, thisFolder);
                    }
                    else
                    {
                        foreach (var file in files.Skip(skipItems))
                        {
                            if (!file.IsCollection)
                                continue;
                            var subFiles = ListFiles(authObject, thisFolder + "/" + file.Name);
                            foreach (var subFile in subFiles)
                                subFile.Name = file.Name + "/" + subFile.Name;
                            tmpFiles.AddRange(subFiles);
                        }

                    }
                    files.AddRange(tmpFiles);
                    if (!tmpFiles.Any(a => a.IsCollection))
                        break; // no more sub folders anymore
                }

                sb.AppendLine("<D:multistatus xmlns:D=\"DAV:\">");

                var first = true;
                foreach (var file in files)
                {
                    var fileName = file.Name.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? file.Name;
                    var folderToUse = first ? parentFolder : thisFolder;
                    first = false;
                    
                    sb.AppendLine("<D:response>");
                    sb.AppendLine("<D:href>" + HttpUtility.HtmlEncode(GetApplicationRootUrl(httpClientData) + (folderToUse == "" ? "" : folderToUse + "/") + file.Name).Replace(" ", "%20") + "</D:href>");
                    {
                        sb.AppendLine("<D:propstat>");
                        {
                            sb.AppendLine("<D:prop>");
                            sb.AppendLine("<D:displayname>" + HttpUtility.HtmlEncode(fileName) + "</D:displayname>");
                            if (!file.IsCollection)
                            {
                                sb.AppendLine("<D:creationdate>" + file.LastModified.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss") + "+00:00</D:creationdate>");
                                sb.AppendLine("<D:getlastmodified>" + file.LastModified.ToUniversalTime().ToString("R") + "</D:getlastmodified>");
                                sb.AppendLine("<D:getcontentlength>" + file.Size + "</D:getcontentlength>");
                                sb.AppendLine("<D:getcontenttype>" + ContentTypeUtil.GetContentTypeFromFileName(file.Name) + "</D:getcontenttype>");
                                sb.AppendLine("<D:resourcetype />");
                            }
                            else
                            {
                                sb.AppendLine("<D:creationdate>" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") + "+00:00</D:creationdate>");
                                sb.AppendLine("<D:getlastmodified>" + DateTime.UtcNow.ToString("R") + "</D:getlastmodified>");
                                sb.AppendLine("<D:getcontentlength />");
                                sb.AppendLine("<D:getcontenttype />");
                                sb.AppendLine("<D:lockdiscovery />");
                                sb.AppendLine("<D:supportedlock />");
                                sb.AppendLine("<D:resourcetype><D:collection/></D:resourcetype>");
                            }
                            
                            sb.AppendLine("</D:prop>");
                        }
                        sb.AppendLine("<D:status>HTTP/1.1 200 OK</D:status>");
                        sb.AppendLine("</D:propstat>");
                    }
                    sb.AppendLine("</D:response>");

                }
                sb.AppendLine("</D:multistatus>");                

                return new HttpHandler.HttpResponse()
                {
                    StatusCode = 207, // Multi status
                    ContentType = "text/xml; charset=\"utf-8\"",
                    Data = ASCIIEncoding.UTF8.GetBytes(sb.ToString()),
                };
            }

            return new HttpHandler.HttpResponse(501, "Not Implemented");
        }

        // Returns the full external link for the current request, or the external link to just the webdav location
        private string GetApplicationRootUrl(HttpHandler.HttpClientData clientData, bool fullRequestPage = false)
        {
            var routePrefix = fullRequestPage ? clientData.RequestPage : clientData.RequestPage.Substring(0, clientData.Request.Length - clientData.RequestPageShort.Length);
            if (!routePrefix.EndsWith("/"))
                routePrefix += "/";

            var host = clientData.Headers.ContainsKey("host") ? clientData.Headers["host"].ToString() : clientData.Host;
            var proto = (clientData.Client.StreamIsEncrypted ? "https" : "http");

            if (clientData.Headers.ContainsKey("x-forwarded-host"))
                host = clientData.Headers["x-forwarded-host"].ToString();
            if (clientData.Headers.ContainsKey("x-forwarded-proto"))
                proto = clientData.Headers["x-forwarded-proto"].ToString();

            return proto + "://" + host + routePrefix;
        }

        // Turns a full request path to just the webdav file path
        private string GetWebDavFilePath(HttpHandler.HttpClientData clientData, string path)
        {
            // Remove the hostname part of the request http://localhost/dav/test.txt -> /dav/test.txt
            if (path.ToLower().StartsWith("http"))
            {
                path = path.Substring(path.IndexOf("//") + 2);
                if (!path.Contains("/"))
                    return "/";
                path = path.Substring(path.IndexOf("/"));
            }

            // We will look at the original request to see what the difference is between the RequestPage and RequestPageShort
            // RequestPage:  /dav/file.txt
            // RequestPageShort: /file.txt

            var beforeRequestPageShort = clientData.RequestPage.Substring(0, clientData.Request.Length - clientData.RequestPageShort.Length);
            if (!beforeRequestPageShort.EndsWith("/"))
                beforeRequestPageShort += "/";
            // /dav/

            if (path.Length > beforeRequestPageShort.Length)
                path = path.Substring(beforeRequestPageShort.Length);
            return HttpUtility.UrlDecode(path);
        }


        public abstract object CheckAuthorization(HttpHandler.HttpClientData httpClientData, string username, string password);
        public abstract WebDavFileContent GetFile(object authObject, string path);
        public abstract WebDavFileInfo GetFileInfo(object authObject, string path);
        public abstract List<WebDavFileInfo> ListFiles(object authObject, string path);        
        public abstract bool DeleteFile(object authObject, string path);
        public abstract bool MoveFile(object authObject, string pathOld, string pathNew);
        public abstract bool PutFile(object authObject, string path, Stream contents, DateTime dateModified);
        public abstract bool MakeCollection(object authObject, string path);
        public abstract bool CopyFile(object authObject, string pathSource, string pathTarget);

        public class WebDavFileInfo
        {
            public string Name { get; set; }
            public bool IsCollection { get; set; }
            public string ContentType { get; set; }
            public long Size { get; set; }
            public DateTime LastModified { get; set; }
        }
        public class WebDavFileContent
        {
            public WebDavFileContent(string localFileName)
            {
                LocalFileName = localFileName;
            }
            public WebDavFileContent(Stream stream, long fileSize, string contentType)
            {
                FileSize = fileSize;
                Stream = stream;
                ContentType = contentType;
            }

            public string? LocalFileName { get; set; }
            public long FileSize { get; set; }
            public Stream Stream { get; set; }
            public string ContentType { get; set; }
        }
    }
}
