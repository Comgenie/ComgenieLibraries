using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers.Http
{
    /// <summary>
    /// This object can be used to define the response the HttpHandler has to send back to the client.
    /// </summary>
    public class HttpResponse
    {
        /// <summary>
        /// Http status code to send to the client. Common ones are 200 OK, 403 Forbidden, 404 Not Found and 500 Internal Server Error
        /// </summary>
        public int StatusCode { get; set; } = 200;

        /// <summary>
        /// Content type of the response. If left empty it will be autodetected by the filename if that one is set.
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Extra response headers to send back to the client.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// If set, this file will be returned to the client. This also allows automatically the client to download a range of the file using the http range headers.
        /// Note: Leave  .Data, .Stream and .ResponseObject empty when using this
        /// </summary>
        public string? FileName { get; set; }

        /// <summary>
        /// If set, this will return the bytes exactly as they are provided as content back to the client.
        /// Note: Leave .FileName, .Stream and .ResponseObject empty when using this
        /// </summary>
        public byte[]? Data { get; set; }

        /// <summary>
        /// If set, this will return the data from the stream starting at the current position (or ContentOffsetStream position) back to the client. Set the ContentLengthStream value to a non-zero value to also automatically set the Content-Length header. 
        /// Note: Leave .FileName, .Data and .ResponseObject empty when using this
        /// </summary>
        public Stream? Stream { get; set; }

        /// <summary>
        /// Set to a non-zero value to populate the Content-Length field when using streams. This prevents the response from being sent as chunked which should also make this request more optimized.
        /// When setting it to a value lower than the total stream length, the response will be truncated to match the given length.
        /// </summary>
        public long ContentLengthStream { get; set; } = -1;

        /// <summary>
        /// If set, this will seek in the stream to this position before sending the stream to the client.
        /// </summary>
        public long ContentOffsetStream { get; set; }

        /// <summary>
        /// If set, this object will be serialized using System.Text.Json and send back to the client. 
        /// Note: Leave .FileName, .Data and .Stream empty when using this
        /// </summary>
        public object? ResponseObject { get; set; }

        /// <summary>
        /// Omit sending a content-length back to the client, and send the response stream as chunked.
        /// </summary>
        public bool ChunkedResponse { get; set; }

        /// <summary>
        /// Set to true to try to GZip the response. This is only done if the browser of the client accepts a gzipped response.
        /// </summary>
        public bool GZipResponse { get; set; }

        /// <summary>
        /// Set to true to directly stop handling this request and not send any response headers/content back. This can be used in cases where custom code already provides a response including headers.
        /// </summary>
        public bool ResponseFinished { get; set; }

        /// <summary>
        /// Can be used to run some clean-up code after a response has been sent.
        /// </summary>
        public Func<HttpClientData, Task>? CallbackResponseSent { get; set; }
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
