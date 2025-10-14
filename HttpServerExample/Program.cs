using Comgenie.Server;
using Comgenie.Server.Handlers;
using Comgenie.Server.Handlers.Http;
using Comgenie.Server.Utils;
using System.Runtime.Intrinsics.Arm;
using System.Text;

namespace HttpServerExample
{
    internal partial class Program
    {
        static void RewindBufferTest()
        {
            var ms = new MemoryStream();
            for (var i=0; i < 10000000; i++)
            {
                ms.WriteByte((byte)('0' + (i % 10)));
            }
            ms.Position = 0;

            var rewindBuffer = new RewindableStream(ms, 555, 155);
            byte[] tmp = new byte[100];

            rewindBuffer.Read(tmp);
            var str = Encoding.ASCII.GetString(tmp);
            Console.WriteLine("RewindableStream: " + str);
            for (var i = 0; i < 100000; i++)
            {
                
                var len = rewindBuffer.Read(tmp);
                if (len < 100)
                {
                    Console.WriteLine(len);
                }
                rewindBuffer.Rewind(30);
                var str2 = Encoding.ASCII.GetString(tmp, 0, len);
                if (str != str2)
                {
                    Console.WriteLine("RewindableStream: " + i + " - " + str + " != " + str2);
                    break;
                }
               
            }

        }
        static void Main(string[] args)
        {
            //RewindBufferTest();
            //return;
            var domain = "yourdomain.here";
            using (var server = new Server())
            {
                /// Always add at least 1 domain, the first domain is also used as default
                server.AddDomain(domain);

                /// Initialize HTTP Handler
                var http = new HttpHandler();

                // Simple static content route
                //http.AddContentRoute(domain, "/", ASCIIEncoding.ASCII.GetBytes("<html><body>Hi there! <script src=\"/js/script.js\"></script></body></html>"), "text/html");

                // File or folder route
                http.AddFileRoute(domain, "/js/*", "./js", null);

                // Application route, note that the method routes are registered with lower case
                http.AddApplicationRoute(domain, "/app", new App(), lowerCaseMethods: false);

                // Reverse proxy route
                http.AddProxyRoute(domain, "/*", "https://miageru.net/");

                // Websocket route
                http.AddWebsocketRoute(domain, "/websocket",
                    connectHandler: async (client) =>
                    {
                        Console.WriteLine("Websocket client connected");
                        await client.SendWebsocketText("Welcome " + client.Client.RemoteAddress);
                    },
                    messageReceivedHandler: async (client, opcode, buffer, offset, len) =>
                    {
                        if (opcode == 1) // text
                        {
                            var str = Encoding.UTF8.GetString(buffer, (int)offset, (int)len);
                            await client.SendWebsocketText(str.ToUpper());
                        }
                    },
                    disconnectHandler: (client) =>
                    {
                        Console.WriteLine("Websocket client disconnected");
                        return Task.CompletedTask;
                    }
                );

                // WebDav route
                http.AddApplicationRoute(domain, "/dav", new WebDavExample());

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
    }
}