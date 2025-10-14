using Comgenie.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers.Http
{
    /// <summary>
    /// File upload data by the client. When using a list of this type as parameter in a Http application, it will automatically be filled if the user uploads files with a matching field name. 
    /// </summary>
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
        /// <summary>
        /// File headers provided by the client.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; }

        /// <summary>
        /// File name as provided by the client, this is not where the path is stored locally on the server. Note that browsers often use fake paths to protect the privacy of the user.
        /// </summary>
        public string? FileName { get; set; }
        public long DataFrom { get; set; }
        public long DataLength { get; set; }
        internal Stream DataStream { get; set; }

        /// <summary>
        /// Get a stream to the uploaded file. 
        /// </summary>
        /// <returns>A stream with the correct length to access the uploaded file.</returns>
        public Stream GetStream()
        {
            // TODO: Also add support for Content-Encoding: base64
            return new SubStream(DataStream, DataFrom, DataLength);
        }
    }
}
