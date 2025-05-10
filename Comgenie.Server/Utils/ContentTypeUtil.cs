using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    /// <summary>
    /// Small utility class to get common content types from file name extensions.
    /// </summary>
    public class ContentTypeUtil
    {
        private static Dictionary<string, string> ContentTypes = new()
        {
            { "htm", "text/html" },
            { "html", "text/html" },
            { "js", "text/javascript" },
            { "css", "text/css" },
            { "csv", "text/csv" },
            { "jpg", "image/jpeg" },
            { "jpeg", "image/jpeg" },
            { "png", "image/png" },
            { "gif", "image/gif" },
            { "webp", "image/webp" },
            { "svg", "image/svg+xml" },
            { "json", "application/json" },
            { "xml", "application/xml" },
            { "txt", "text/plain" },
            { "mp4", "video/mp4" },
            { "mkv", "video/x-matroska" }
        };

        /// <summary>
        /// Gets the most appropiate content type from the file name extension.
        /// </summary>
        /// <param name="fileName">File name or path including extension</param>
        /// <returns>A mime type which can be used for the Content-Type header</returns>
        public static string GetContentTypeFromFileName(string fileName)
        {
            var ext = Path.GetExtension(fileName.ToLower() ?? "").TrimStart('.');
            if (ContentTypes.TryGetValue(ext, out var contentType))
                return contentType;
            return "application/octet-stream";
        }
    }
}
