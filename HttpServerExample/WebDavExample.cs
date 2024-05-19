using Comgenie.Server.Handlers;
using Comgenie.Server.HttpApplications;
using System.Text;

namespace HttpServerExample
{

    public class WebDavExample : WebDavHttpApplication
    {
        public override object CheckAuthorization(HttpHandler.HttpClientData httpClientData, string username, string password)
        {
            // Return null to trigger a 401 Unauthorized response with a Basic realm header.
            // Return any other object to pass it to the other methods.
            // Note that windows does require an SSL connection for basic authentication.
            return true;
        }

        public override bool DeleteFile(object authObject, string path)
        {
            return true;
        }

        public override WebDavFileContent GetFile(object authObject, string path)
        {
            if (path == "Example Folder/Example File.txt")
            {
                return new WebDavFileContent(new MemoryStream(Encoding.ASCII.GetBytes("Just a test file")), 16, "text/plain");

                // When returning actual files from disk, use the following code instead (this also adds support for retrieving ranges)
                // return new WebDavFileContent("path\\to\\actual\\file.txt");
            }
            return null;
        }
        public override WebDavFileInfo GetFileInfo(object authObject, string path)
        {
            if (path == "Example Folder/Example File.txt")
            {
                return new WebDavFileInfo()
                {
                    IsCollection = false,
                    Name = path,
                    Size = 16,
                    ContentType = "text/plain",
                };
            }
            return null;
        }
        public override List<WebDavFileInfo> ListFiles(object authObject, string path)
        {
            var list = new List<WebDavFileInfo>();
            if (path == "")
            {
                list.Add(new WebDavFileInfo()
                {
                    IsCollection = true,
                    Name = "Example Folder"
                });
            }
            else if (path == "Example Folder")
            {
                list.Add(new WebDavFileInfo()
                {
                    IsCollection = false,
                    LastModified = DateTime.UtcNow,
                    Name = "Example File.txt",
                    Size = 16
                });
            }

            return list;
        }

        public override bool MakeCollection(object authObject, string path)
        {
            // Return true to indicate success
            return true;
        }

        public override bool MoveFile(object authObject, string pathOld, string pathNew)
        {
            // Return true to indicate success
            return true;
        }
        public override bool CopyFile(object authObject, string pathSource, string pathTarget)
        {
            // Return true to indicate success
            return true;
        }

        public override bool PutFile(object authObject, string path, Stream contents, DateTime dateModified)
        {
            // Return true to indicate success
            return true;
        }
    }
}