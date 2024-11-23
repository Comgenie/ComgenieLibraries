using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.Http.HttpHandler;

namespace Comgenie.Server.Handlers.Http
{
    public partial class HttpHandler
    {
        public void AddContentRoute(string domain, string path, byte[] contents, string contentType)
        {
            AddRoute(domain, path, new Route()
            {
                HandleExecuteRequest = (client, data) => {
                    return new HttpResponse()
                    {
                        StatusCode = 200,
                        ContentType = contentType,
                        Data = contents
                    };
                }
            });
        }
    }
}
