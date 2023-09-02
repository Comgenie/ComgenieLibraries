using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    public class ContentTypeUtil
    {
        public static string GetContentTypeFromFileName(string fileName)
        {
            if (fileName == null)
                return "application/octet-stream";
            var ext = Path.GetExtension(fileName.ToLower()).Replace(".", "");
            if (ext == "htm" || ext == "html")
                return "text/html";
            else if (ext == "js")
                return "text/javascript";
            else if (ext == "css")
                return "text/css";
            else if (ext == "csv")
                return "text/csv";
            else if (ext == "jpg" || ext == "jpeg")
                return "image/jpeg";
            else if (ext == "png")
                return "image/png";
            else if (ext == "gif")
                return "image/gif";
            else if (ext == "webp")
                return "image/webp";
            else if (ext == "svg")
                return "image/svg+xml";
            else if (ext == "json")
                return "application/json";
            else if (ext == "xml")
                return "application/xml";
            else if (ext == "txt")
                return "text/plain";
            else if (ext == "mp4")
                return "video/mp4";
            else if (ext == "mk4")
                return "video/x-matroska";
            return "application/octet-stream";
        }
    }
}
