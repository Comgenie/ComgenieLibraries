using Comgenie.Server.Handlers;
using Comgenie.Server.Handlers.Imap;
using Comgenie.Server.Handlers.Smtp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server
{
    public class Server : IDisposable
    {
        public const int MaxPacketSize = 1024 * 32;

        private string? PfxKey = null;
        public string? DefaultDomain { get; set; }

        // The server handler will listen to all ports, accept connections and handle SSL
        // It will then forward the data to any connectionhandlers attached
        private Dictionary<Socket, ServerProtocol> Handlers = new Dictionary<Socket, ServerProtocol>();
        private static Dictionary<string, X509Certificate> ServerCertificates = new Dictionary<string, X509Certificate>();
        public static HashSet<string> IPBanList = new HashSet<string>();

        private static List<Server>? ActiveInstances { get; set; }
        private static Thread? CleanUpThread { get; set; }
        public HashSet<string> Domains { get; set; }
        
        public bool IsActive = true;
        public ConcurrentStack<byte[]> Buffers = new ConcurrentStack<byte[]>();
        public List<Client> Clients = new List<Client>();
        
        public static void SaveBanList()
        {            
            File.WriteAllText("IPBanList.json", JsonSerializer.Serialize(IPBanList));
        }
        public static void LoadBanList()
        {
            if (File.Exists("IPBanList.json"))
            {
                var ipBanList = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText("IPBanList.json"));
                if (ipBanList != null)
                    IPBanList = ipBanList;
            }
        }

        public Server()
        {
            Domains = new HashSet<string>();
            if (ActiveInstances == null)
            {
                ActiveInstances = new List<Server>();
                CleanUpThread = new Thread(new ThreadStart(() => {
                    while (true)
                    {
                        List<Server>? instances = null;

                        lock (ActiveInstances)
                            instances = ActiveInstances.ToList();
                        
                        foreach (Server instance in instances)
                            instance.CleanUpOldClients();

                        Thread.Sleep(10 * 1000);
                    }
                }));                
                CleanUpThread.Start();
            }
            lock (ActiveInstances)
                ActiveInstances.Add(this);
        }

        public void AddDomain(string domain, bool setAsDefault = false)
        {
            domain = domain.ToLower();
            lock (ServerCertificates)
            {
                if (PfxKey == null)
                {
                    // By default we will generate a key file containing the .pfx key for this application on this machine
                    // This will of course not be done if there is a key already specified using SetPfxKey before adding any domains
                    if (File.Exists("do-not-share.key"))
                        PfxKey = File.ReadAllText("do-not-share.key");
                    else
                    {
                        PfxKey = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 12);
                        File.WriteAllText("do-not-share.key", PfxKey);
                    }
                }

                // Load certificate (or create self signed if we don't have any yet). The LetsEncrypt tool can replace this for us
                if (!File.Exists(domain + ".pfx"))
                    GenerateSelfSignedCertificate(domain);

                var certificate = X509CertificateLoader.LoadPkcs12FromFile(domain + ".pfx", PfxKey);// new X509Certificate2(domain + ".pfx", PfxKey);
                if (DateTime.UtcNow > certificate.NotAfter)
                {
                    // Certificate is not valid anymore, generate a new self-signed one
                    certificate.Dispose();
                    GenerateSelfSignedCertificate(domain);
                    certificate = X509CertificateLoader.LoadPkcs12FromFile(domain + ".pfx", PfxKey);
                }

                if (ServerCertificates.ContainsKey(domain)) // Reload           
                    ServerCertificates[domain] = certificate;
                else
                    ServerCertificates.Add(domain, certificate);

                if (DefaultDomain == null || setAsDefault) // First domain is always added as default
                    DefaultDomain = domain;

                Domains.Add(domain);
            }
        }

        public void SetPfxKey(string newKey)
        {
            this.PfxKey = newKey;
        }
        internal string? GetPfxKey()
        {
            return this.PfxKey;
        }

        public void Listen(int port, bool ssl, IConnectionHandler handler)
        {
            LoadBanList();

            Socket listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
            listenSocket.Listen(1024);

            Handlers.Add(listenSocket, new ServerProtocol(handler, port, ssl, (handler is SmtpHandler || handler is ImapHandler)));
            StartAcceptThread(listenSocket, port);
        }
        public void ListenUnixDomainSocket(string socketFile, IConnectionHandler handler)
        {
            if (!socketFile.Contains("/") && !socketFile.Contains("\\"))
                socketFile = Path.Combine(Path.GetTempPath(), socketFile); // No path provided, so default to temp folder

            if (File.Exists(socketFile))
                File.Delete(socketFile);

            Socket listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            listenSocket.Bind(new UnixDomainSocketEndPoint(socketFile));
            listenSocket.Listen(1024);

            Handlers.Add(listenSocket, new ServerProtocol(handler, 0, false, (handler is SmtpHandler || handler is ImapHandler)));
            StartAcceptThread(listenSocket, 0);
        }
        private void StartAcceptThread(Socket listenSocket, int port)
        {
            var acceptThread = new Thread(new ThreadStart(() =>
            {
                while (IsActive)
                {
                    try
                    {
                        var clientSocket = listenSocket.Accept();
                        if (clientSocket == null)
                            break;
                        _ = InitConnection(listenSocket, clientSocket);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(nameof(Server), "Could not initialize connection (port " + port + "): " + ex.Message);
                    }
                }
            }));
            acceptThread.Start();
        }

        async Task InitConnection(Socket listenSocket, Socket clientSocket)
        {
            string clientIp = "@"; // uds fallback
            int remotePort = 0;

            if (clientSocket.RemoteEndPoint != null && clientSocket.RemoteEndPoint is IPEndPoint remoteEndPoint)
            {
                clientIp = remoteEndPoint.Address.ToString();
                remotePort = remoteEndPoint.Port;
            }
            else if (clientSocket.RemoteEndPoint == null)
            {
                Log.Info(nameof(Server), "Socket might not be connected anymore");
                try
                {
                    clientSocket.Close();
                }
                catch { }

                return;
            }

            if (IPBanList.Contains(clientIp))
            {
                Log.Info(nameof(Server), "Refused connection from banned ip " + clientIp);
                clientSocket.Close();
                return;
            }

            var protocol = Handlers[listenSocket];

            var client = new Client()
            {
                Server = this,
                Socket = clientSocket,
                Handler = protocol.Handler,
                ReadOneByOne = protocol.ReadOneByOne,
                ConnectMoment = DateTime.UtcNow,
                LastDataReceivedMoment = DateTime.UtcNow,
                LastDataSentMoment = DateTime.UtcNow,
                RemoteAddress = clientIp,
                RemoteAddressPort = remotePort
            };

            client.NetworkStream = new NetworkStream(clientSocket);

            if (protocol.Ssl)
            {
                EnableSSLOnClient(client);
            }
            else
            {
                client.Stream = client.NetworkStream;
                client.StreamIsReady = true; // No handshake required
                await client.Handler.ClientConnect(client);
                _ = client.Read();
                //StartReadTask(client);
            }
        }
        /*public void StartReadTask(Client client)
        {                       
            var clientSocket = client.Socket;
            var task = Task.Run(async () =>
            {
                Log.Debug(nameof(Server), "[ReadTask] Start handling client");

                try
                {
                    client.StreamReadingCancellationTokenSource = new CancellationTokenSource(); // NOTE: We cannot use this in all situations as it automatically disconnects the socket if its triggered during ReadAsync.. Check if fixed by MS in the future

                    var processingCount = 0;
                    while (clientSocket.Connected && IsActive)
                    {
                        try
                        {
                            if (client.StreamIsReady && processingCount < client.MaxProcessingCount) 
                            {
                                byte[] buffer;
                                if (!Buffers.TryPop(out buffer))
                                    buffer = new byte[MaxPacketSize];
                                
                                client.Stream.ReadTimeout = -1;                                
                                var len = await client.Stream.ReadAsync(buffer, 0, buffer.Length, client.StreamReadingCancellationTokenSource.Token);
                                if (len == 0 || client.Stream == null)
                                {
                                    Log.Debug(nameof(Server), "[ReadTask] No more data, len: " + len);
                                    break;
                                }

                                processingCount++;
                                client.AddIncomingBufferData(buffer, len, () =>
                                {
                                    // Give buffer back to pool (unless the pool is too large)
                                    if (Buffers.Count < 100)
                                        Buffers.Push(buffer);                                    
                                    processingCount--;
                                });                                                      
                            }
                            else
                            {
                                await Task.Delay(25);
                            }
                        } 
                        catch (OperationCanceledException e) // Cancelled read
                        {
                            client.StreamReadingCancellationTokenSource = new CancellationTokenSource();
                            Log.Debug(nameof(Server), "[ReadTask] Could not read client stream Exception [2]  (Client connected " + clientSocket.Connected + "): " + e);
                        }
                        
                        if (client.CancellationCallBack != null)
                        {
                            Log.Debug(nameof(Server), "[ReadTask] Calling cancellation callback (Client connected " + clientSocket.Connected + ")");

                            client.CancellationCallBack();
                            client.CancellationCallBack = null;
                            client.StreamReadingCancellationTokenSource = new CancellationTokenSource();
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Debug(nameof(Server), "[ReadTask] ould not read client stream Exception: " + e);
                }


                Log.Debug(nameof(Server), "[ReadTask] Stopping to handle client (Client connected " + clientSocket.Connected + ")");

                await client.Disconnect();

                lock (Clients)
                    Clients.Remove(client);

                Log.Debug(nameof(Server), "[ReadTask] Stop handling client");
            });

            lock (Clients)
                Clients.Add(client);
        }*/
        public void EnableSSLOnClient(Client client, string? preferDomain=null, Action? callBackStreamReadingStopped = null)
        {
            var isUpgradedConnection = client.Stream != null;

            var certificateSelection = new ServerCertificateSelectionCallback((sender, hostName) =>
            {
                if (ServerCertificates.Count == 0)
                    return null!;
                if (hostName != null && ServerCertificates.ContainsKey(hostName))
                    return ServerCertificates[hostName];
                if (preferDomain != null && ServerCertificates.ContainsKey(preferDomain))
                    return ServerCertificates[preferDomain];
                if (DefaultDomain != null)
                    return ServerCertificates[DefaultDomain];
                return ServerCertificates.First().Value;
            });

            client.StreamIsReady = false; // This stops the read thread from reading the stream in the meantime

            // If the CancellationTokenSource is fixed in the future, we can use this. Now we have to use MaxProcessingCount == 1 to make Upgrading SSL work
            /*if (client.StreamReadingCancellationTokenSource != null) 
            {                                
                bool waiting = true;
                client.CancellationCallBack = () => {
                    waiting = false;
                };

                client.StreamReadingCancellationTokenSource.Cancel();

                while (waiting)
                    Thread.Sleep(10);                
            } */

            if (callBackStreamReadingStopped != null)
                callBackStreamReadingStopped();
            
            
            var ssl = new SslStream(client.NetworkStream!, false);
            client.Stream = ssl;
            client.StreamIsEncrypted = true;

            var myTask = Task.Run(async () =>
            {
                Log.Debug(nameof(Server), "Run task SSL");
                Stopwatch sw = new Stopwatch();
                sw.Start();
                try
                {
                    /*var allowedCipherSuite = new List<TlsCipherSuite>(); // Only works on TLS 1.3
                    allowedCipherSuite.Add(TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384);
                    allowedCipherSuite.Add(TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256);
                    allowedCipherSuite.Add(TlsCipherSuite.TLS_DHE_RSA_WITH_AES_256_GCM_SHA384);
                    allowedCipherSuite.Add(TlsCipherSuite.TLS_DHE_RSA_WITH_AES_128_GCM_SHA256); */
                    client.Stream.ReadTimeout = 10000;                    
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions()
                    {
                        ServerCertificateSelectionCallback = certificateSelection,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12 /*| System.Security.Authentication.SslProtocols.Tls11*/,
                        //CipherSuitesPolicy = new CipherSuitesPolicy(allowedCipherSuite),
                        EncryptionPolicy = EncryptionPolicy.RequireEncryption
                    });
                }
                catch (Exception e)
                {
                    Log.Debug(nameof(Server), "Error during ssl handshake, disconnecting client: " + e.Message);
                    try
                    {
                        ssl.Close();
                        // Disconnect client
                        if (isUpgradedConnection)
                        {
                            await client.Disconnect();
                        }
                        else if (client.Socket != null)
                        {
                            client.Socket.Close();
                        }
                        //client.Socket.Close();
                    }
                    catch { }

                    return;
                }
                sw.Stop();
                Log.Debug(nameof(Server), "Marking client as ready in " + sw.ElapsedMilliseconds + "ms");
                client.StreamIsReady = true; // Stream can now be read to get the unencrypted data

                if (!isUpgradedConnection)
                {
                    await client.Handler.ClientConnect(client);
                    // StartReadTask(client);
                    _ = client.Read();
                }                
            });
        }

        private void GenerateSelfSignedCertificate(string domain)
        {
            using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            { 
                // generate asymmetric key pair
                var req = new CertificateRequest("cn=" + domain, ecdsa, HashAlgorithmName.SHA256);
                var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(5));
                // Create PFX (PKCS #12) with private key
                File.WriteAllBytes(domain + ".pfx", cert.Export(X509ContentType.Pfx, PfxKey));

                // Create Base 64 encoded CER (public key only)
                /*File.WriteAllText(domain + ".cer",
                    "-----BEGIN CERTIFICATE-----\r\n"
                    + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)
                    + "\r\n-----END CERTIFICATE-----");*/
            }
        }

        public void CleanUpOldClients(int noActivitySeconds = 120)
        {
            var inactiveMoment = DateTime.UtcNow.AddSeconds(-noActivitySeconds);

            List<Client>? inactiveClients = null;
            lock (Clients)
                inactiveClients = Clients.Where(c => c.LastDataReceivedMoment < inactiveMoment && c.LastDataSentMoment < inactiveMoment).ToList();

            foreach (var client in inactiveClients)
                client.Disconnect().Wait(); // This stops all read tasks
        }
        public void Dispose()
        {
            IsActive = false;
            lock (ActiveInstances!)
                ActiveInstances.Remove(this);

            foreach (var listenSocket in Handlers)
                listenSocket.Key.Close(); // Stop accepting new connections
            foreach (var client in Clients)
                client.Disconnect().Wait(); // This stops all read tasks            
        }

        class ServerProtocol
        {
            public int Port { get; set; }
            public bool Ssl { get; set; }
            public bool ReadOneByOne { get; set; }
            public IConnectionHandler Handler { get; set; }
            public ServerProtocol(IConnectionHandler handler, int port, bool ssl = false, bool readOneByOne = false)
            {
                Port = port;
                Ssl = ssl;
                ReadOneByOne = readOneByOne;
                Handler = handler;
            }
        }
    }
}
