using Microsoft.VisualBasic;
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
        public void AddFileRoute(string domain, string path, string localPath, string? contentType)
        {
            AddRoute(domain, path, new Route()
            {
                HandleExecuteRequest = (client, data) => {
                    if (data.RequestPageShort == null)
                        return null;

                    var response = new HttpResponse();
                    string? requestedFile = null;

                    if (Directory.Exists(localPath)) // Route is linking to a directory
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

                        var requestedLocalPath = Path.Combine(localPath, data.RequestPageShort);

                        // Second check against escaping the content folder
                        if (!Path.GetFullPath(requestedLocalPath).StartsWith(Path.GetFullPath(localPath)))
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
                    else if (File.Exists(localPath)) // Route is linking to a file directly
                        requestedFile = localPath;

                    if (requestedFile != null)
                    {
                        response = new HttpResponse()
                        {
                            FileName = requestedFile
                        };
                    }
                    return response;
                }
            });
        }
    }
}
