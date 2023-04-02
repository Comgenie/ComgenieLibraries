using Comgenie.Server;
using Comgenie.Server.Handlers;
using Comgenie.Server.Utils;
using System.Runtime.Intrinsics.Arm;
using System.Text;

namespace HttpServerExample
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var domain = "yourdomain.here";
            using (var server = new Server())
            {
                /// Always add at least 1 domain, the first domain is also used as default
                server.AddDomain(domain);

                /// Initialize HTTP Handler
                var http = new HttpHandler();

                // Simple static content route
                http.AddContentRoute(domain, "/", ASCIIEncoding.ASCII.GetBytes("<html><body>Hi there! <script src=\"/js/script.js\"></script></body></html>"), "text/html");

                // File or folder route
                http.AddFileRoute(domain, "/js/*", "./js", null);

                // Application route, note that the method routes are registered with lower case
                http.AddApplicationRoute(domain, "/app", new App(), lowerCaseMethods: false);

                // Reverse proxy route
                // http.AddProxyRoute(domain, "/proxy", "https://...")

                // Websocket route
                http.AddWebsocketRoute(domain, "/websocket",
                    connectHandler: (client) =>
                    {
                        Console.WriteLine("Websocket client connected");
                        client.SendWebsocketText("Welcome " + client.Client.RemoteAddress);
                    },
                    messageReceivedHandler: (client, opcode, buffer, offset, len) =>
                    {
                        if (opcode == 1) // text
                        {
                            var str = Encoding.UTF8.GetString(buffer, (int)offset, (int)len);
                            client.SendWebsocketText(str.ToUpper());
                        }
                    },
                    disconnectHandler: (client) =>
                    {
                        Console.WriteLine("Websocket client disconnected");
                    }
                );

                /// Start listening to http and https
                server.Listen(80, false, http);
                server.Listen(443, true, http);

                /// Generate a valid SSL certificate using LetsEncrypt (otherwise by default a self signed is used)
                // Note: Only uncomment these following lines if you agree to the LetsEncrypt terms of service at https://letsencrypt.org/repository/
                //var letsEncryptUtil = new LetsEncryptUtil(server, http, "your.email@address.here", false);
                //letsEncryptUtil.CheckAndRenewAllServerDomains();

                /// Keep this application running
                Console.WriteLine("Server started. Press enter to exit");
                Console.ReadLine();
            }
        }

        class App
        {
            // /app
            public HttpHandler.HttpResponse Index(HttpHandler.HttpClientData httpClientData)
            {
                return new HttpHandler.HttpResponse()
                {
                    StatusCode = 200,
                    Data = Encoding.UTF8.GetBytes("Hi welcome at /app !")
                };
            }

            // /app/ReverseText
            public HttpHandler.HttpResponse ReverseText(HttpHandler.HttpClientData httpClientData, string text = "default value")
            {
                return new HttpHandler.HttpResponse()
                {
                    StatusCode = 200,
                    ContentType = "text/plain",
                    Data = Encoding.UTF8.GetBytes(text.Reverse().ToArray())
                };                    
            }

            // /app/TimesTwo
            public HttpHandler.HttpResponse TimesTwo(HttpHandler.HttpClientData httpClientData, ExampleDTO dto)
            {
                if (dto == null)
                    return new HttpHandler.HttpResponse(400, "Missing object");

                dto.Number *= 2;
                return new HttpHandler.HttpResponse(200, dto);
            }

            // /app/AllOtherMethods
            public HttpHandler.HttpResponse Other(HttpHandler.HttpClientData httpClientData)
            {
                return new HttpHandler.HttpResponse()
                {
                    StatusCode = 200,
                    Data = Encoding.UTF8.GetBytes("Gonna catch them all")
                };
            }
        }

        class ExampleDTO
        {
            public int Number { get; set; }
        }
    }
}