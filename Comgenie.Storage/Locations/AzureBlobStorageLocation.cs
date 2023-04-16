using Comgenie.Storage.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace Comgenie.Storage.Locations
{
    public class AzureBlobStorageLocation : IStorageLocation
    {
        // TODO: Use page blobs instead of block blobs
        private string SasUrl { get; set; }
        public AzureBlobStorageLocation() { }
        public AzureBlobStorageLocation(string containerSasUrl)
        {
            SetConnection(containerSasUrl);
        }

        public void DeleteFile(string path)
        {
            // Use a HTTP DELETE request to delete the blob from the storage account using the SAS URL.
            var url = SasUrl.Substring(0, SasUrl.IndexOf("?")) + "/" + path + SasUrl.Substring(SasUrl.IndexOf("?"));

            using (var httpClient = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Delete, url))
            {
                request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
                var response = httpClient.SendAsync(request).Result;
                if (!response.IsSuccessStatusCode)
                    throw new Exception("Error when deleting file from Azure: " + response.StatusCode); 
            }
        }

        public bool IsAvailable()
        {
            // List the blobs in the container using the SAS URL.
            using (var httpClient = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Get, SasUrl + "&comp=list&restype=container");
                request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
                var response = httpClient.SendAsync(request).Result;
                if (!response.IsSuccessStatusCode)
                    return false;
            }
            return true;
        }

        public void SetConnection(string connectionString)
        {
            SasUrl = connectionString;
        }
        private async Task UploadBlockBlobFromStream(string url, Stream stream)
        {
            var blockIdCount = 0;

            var buffer = new byte[1024 * 1024 * 10];
            var bufferOffset = 0;
            long totalOffset = 0;
            StringBuilder putListRequest = new StringBuilder();
            putListRequest.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            putListRequest.AppendLine("<BlockList>");
            while (true)
            {
                var len = stream.Read(buffer, 0, buffer.Length);
                if (len > 0)
                    bufferOffset += len;

                if (len != 0 && len < buffer.Length)
                    continue;

                if (bufferOffset == 0)
                    break;

                blockIdCount++;

                var base64blockid = Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes(blockIdCount.ToString("D8")));
                putListRequest.AppendLine("<Latest>" + base64blockid + "</Latest>");

                using (var httpClient = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Put, url + "&comp=block&blockid=" + HttpUtility.UrlEncode(base64blockid)))
                {
                    request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
                    request.Content = new ByteArrayContent(buffer, 0, bufferOffset);

                    var response = await httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("Error when uploading file to Azure: " + response.StatusCode); // TODO: raise this exception at stream writer level
                }
                totalOffset += bufferOffset;
                bufferOffset = 0;
                if (len == 0)
                    break; // finished
            }
            putListRequest.AppendLine("</BlockList>");

            // Do Put block list request
            using (var httpClient = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Put, url + "&comp=blocklist"))
            {
                request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
                request.Content = new StringContent(putListRequest.ToString());

                var response = await httpClient.SendAsync(request);
                var message = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
                if (!response.IsSuccessStatusCode)
                    throw new Exception("Could not commit block list: " + message);
            }
        }
        public Stream? OpenFile(string path, FileMode mode, FileAccess access)
        {
            var url = SasUrl.Substring(0, SasUrl.IndexOf("?")) + "/" + path + SasUrl.Substring(SasUrl.IndexOf("?"));

            if (access == FileAccess.Read)
            {
                // Just download
                using (var httpClient = new HttpClient())
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));

                    var response = httpClient.SendAsync(request).Result;
                    if (!response.IsSuccessStatusCode)
                        return null;

                    return response.Content.ReadAsStream();
                }
            }
            else if (access == FileAccess.Write)
            {
                var stream = new ForwardStream()
                {
                    CaptureWriter = true
                };

                // Create block blob                
                using (var httpClient = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Put, url))
                {
                    request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
                    request.Headers.Add("x-ms-blob-type", "BlockBlob");
                    //request.Headers.Add("x-ms-blob-content-length", (1024 * 1024 * 10).ToString()); // initial size
                    request.Content = new StringContent("");

                    var response = httpClient.SendAsync(request).Result;
                    var message = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("Could not create page blob in azure, make sure the SAS url permissions are set correctly: " + message);
                    
                }
                
                Task.Run(async () =>
                {
                    await UploadBlockBlobFromStream(url, stream);
                    // We're finished, release the writer
                    stream.ReleaseWriter();
                });
                return stream;
            }
            else if (access == FileAccess.ReadWrite)
            {
                // Download all, upload when finished
                // TODO: When still using block blobs, retrieve the block blob list to only retrieve and update the blocks we're accessing
                var tmpfile = Path.GetTempFileName();
                var stream = File.Open(tmpfile, FileMode.Create, FileAccess.ReadWrite);

                using (var httpClient = new HttpClient())
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));

                    var response = httpClient.SendAsync(request).Result;
                    if (!response.IsSuccessStatusCode)
                        return null;

                    response.Content.ReadAsStream().CopyTo(stream);
                }
                stream.Position = 0;

                var fileIsChanged = false;
                var callBackStream = new CallbackStream(stream);
                
                callBackStream.OnWrite = (buffer, offset, len) =>
                {
                    fileIsChanged = true;
                };

                callBackStream.OnDispose = () => {
                    if (!fileIsChanged)
                        return;

                    // Upload changed file
                    callBackStream.Position = 0;
                    UploadBlockBlobFromStream(url, callBackStream).Wait();
                };

                return callBackStream;
            }
            return null;
        }
    }
}
