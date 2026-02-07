using Comgenie.Server.Handlers;
using Comgenie.Server.Handlers.Imap;
using Comgenie.Server.Handlers.Smtp;
using Comgenie.Util;
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
    /// <summary>
    /// Main Comgenie.Server instance. This is responsible for listening to a port, accepting connections and enabling the TLS connection 
    /// before handing them off to the attached handler.
    /// </summary>
    public class Server : IDisposable
    {
        internal const int MaxPacketSize = 1024 * 32;

        /// <summary>
        /// Key used to protect certificates.
        /// If none is set when adding a domain, a random key will be chosen and saved in the secrets folder in the fole do-not-share.key.
        /// </summary>
        public string? PfxKey { get; set; } = null;

        /// <summary>
        /// Used as fallback if a matching domain could not be found (for example for incoming http requests).
        /// If none is set, the first domain added with .AddDomain() is used.
        /// </summary>
        public string? DefaultDomain { get; set; }

        /// <summary>
        /// Current added domains. Note: Only add domains using the .AddDomain() method.
        /// </summary>
        public HashSet<string> Domains { get; internal set; } = new();

        /// <summary>
        /// Current connected clients.
        /// </summary>
        public List<Client> Clients { get; set; } = new();

        // The server handler will listen to all ports, accept connections and handle SSL
        // It will then forward the data to any connectionhandlers attached
        private Dictionary<Socket, ServerProtocol> Handlers = new Dictionary<Socket, ServerProtocol>();


        internal bool IsActive = true;
        internal ConcurrentStack<byte[]> Buffers = new ConcurrentStack<byte[]>();

        private static Dictionary<string, X509Certificate> ServerCertificates = new Dictionary<string, X509Certificate>();
        private static List<Server>? ActiveInstances { get; set; }
        private static Thread? CleanUpThread { get; set; }

        private Func<string, Socket, bool>? IncomingConnectionShouldAcceptHandler { get; set; }

        /// <summary>
        /// Set a custom handler to control if an incoming connection should be accepted or not.
        /// This can be used to implement a ban-list. 
        /// Note that due to OS limits a rejected connection is actually accepted and directly disconnected.
        /// </summary>
        /// <param name="incomingConnectionShouldAcceptHandler">The handler bool (ip, clientSocket), return true to accept the connection</param>
        public void SetIncomingConnectionShouldAcceptHandler(Func<string, Socket, bool>? incomingConnectionShouldAcceptHandler)
        {
            IncomingConnectionShouldAcceptHandler = incomingConnectionShouldAcceptHandler;
        }

        /// <summary>
        /// Initializes a new instance of the Comgenie.Server.
        /// This is responsible for accepting for incoming connections, completing tls handshakes and forwarding data to the correct handlers.
        /// </summary>
        public Server()
        {
            if (ActiveInstances == null)
            {
                // Create a single clean-up thread for all server instances
                ActiveInstances = new List<Server>();
                CleanUpThread = new Thread(new ThreadStart(() => {
                    while (true)
                    {
                        List<Server>? instances = null;

                        lock (ActiveInstances)
                            instances = ActiveInstances.ToList();
                        
                        foreach (Server instance in instances)
                            instance.CleanUpOldClients();

                        // TODO: Also reload certificates once in a while as they can also be changed from other processes

                        Thread.Sleep(10 * 1000);
                    }
                }));                
                CleanUpThread.Start();
            }
            lock (ActiveInstances)
                ActiveInstances.Add(this);
        }

        /// <summary>
        /// Adds a domain to this server instance and optionally set it as default (used as fallback)
        /// This also loads the certificate for each domain added, and generates a self-signed one in case none is found in the secrets folder.
        /// </summary>
        /// <param name="domain">Domain name</param>
        /// <param name="setAsDefault">If set to true this domain will be used in case no matching domain can be found for incoming requests.</param>
        public void AddDomain(string domain, bool setAsDefault = false)
        {
            domain = domain.ToLower();
            lock (ServerCertificates)
            {
                if (!Directory.Exists(GlobalConfiguration.SecretsFolder))
                    Directory.CreateDirectory(GlobalConfiguration.SecretsFolder);
                
                if (PfxKey == null)
                {
                    var pfxKeyPath = Path.Combine(GlobalConfiguration.SecretsFolder, "do-not-share.key");

                    // By default we will generate a key file containing the .pfx key for this application on this machine
                    // This will of course not be done if there is a key already specified using SetPfxKey before adding any domains
                    if (File.Exists(pfxKeyPath))
                        PfxKey = File.ReadAllText(pfxKeyPath);
                    else
                    {
                        PfxKey = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 12);
                        File.WriteAllText(pfxKeyPath, PfxKey);
                    }
                }

                // Load certificate (or create self signed if we don't have any yet). The LetsEncrypt tool can replace this for us
                var certificatePath = Path.Combine(GlobalConfiguration.SecretsFolder, domain + ".pfx");

                if (!File.Exists(certificatePath))
                    File.WriteAllBytes(certificatePath, GenerateSelfSignedCertificate(domain));

                var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, PfxKey);// new X509Certificate2(domain + ".pfx", PfxKey);
                if (DateTime.UtcNow > certificate.NotAfter)
                {
                    // Certificate is not valid anymore, generate a new self-signed one
                    certificate.Dispose();
                    File.WriteAllBytes(certificatePath, GenerateSelfSignedCertificate(domain));
                    certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, PfxKey);
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

        /// <summary>
        /// Start listening on a tcp/ip port, optionally handle the tls handshake and forward the incoming data to the connection handler.
        /// </summary>
        /// <param name="port">Port to listen on</param>
        /// <param name="ssl">If set to true, this connection will be handled as a tls connection and a handshake is automatically done using the certificate in the secrets folder.</param>
        /// <param name="handler">The handler to forward the incoming data to</param>
        public void Listen(int port, bool ssl, IConnectionHandler handler)
        {
            Socket listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
            listenSocket.Listen(1024);

            Handlers.Add(listenSocket, new ServerProtocol(handler, port, ssl, (handler is SmtpHandler || handler is ImapHandler)));
            StartAcceptThread(listenSocket, port);
        }

        /// <summary>
        /// Start listening using an unix domain socket file.
        /// </summary>
        /// <param name="socketFile">Path to a local file path. The path has to be below 100 characters. Note that this file will be deleted if it exists already!</param>
        /// <param name="handler">The handler to forward the incoming data to</param>
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
                        _ = InitConnectionAsync(listenSocket, clientSocket);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(nameof(Server), "Could not initialize connection (port " + port + "): " + ex.Message);
                    }
                }
            }));
            acceptThread.Start();
        }

        private async Task InitConnectionAsync(Socket listenSocket, Socket clientSocket)
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

            if (IncomingConnectionShouldAcceptHandler != null && !IncomingConnectionShouldAcceptHandler(clientIp, clientSocket))
            {
                Log.Info(nameof(Server), "Rejected connection from ip " + clientIp);
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
                RemoteAddressPort = remotePort,
                CancellationTokenSource = new CancellationTokenSource()
            };
            client.ResetTimeout();

            client.NetworkStream = new NetworkStream(clientSocket);

            if (protocol.Ssl)
            {
                EnableSSLOnClient(client, cancellationToken: client.CancellationTokenSource.Token);
            }
            else
            {
                client.Stream = client.NetworkStream;
                client.StreamIsReady = true; // No handshake required
                await client.Handler.ClientConnectAsync(client, client.CancellationTokenSource.Token);
                _ = client.ReadAsync(client.CancellationTokenSource.Token);
            }
        }

        /// <summary>
        /// Start handshake process with client.
        /// Normally this is done automatically if 'ssl' was set to true in the .Listen call.
        /// But in some situations (StartTLS in smtp protocol) it needs to be done on demand.
        /// </summary>
        /// <param name="client">Connected client to start the handshake with</param>
        /// <param name="preferDomain">If the client sends a domain which we don't recognize, we will use this one instead (and the DefaultDomain one after that).</param>
        /// <param name="callBackStreamReadingStopped">After all waiting reading processes stopped, this can be used to send a message before the actual handshake starts.</param>
        public void EnableSSLOnClient(Client client, string? preferDomain=null, Action? callBackStreamReadingStopped = null, CancellationToken cancellationToken = default)
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

            if (callBackStreamReadingStopped != null)
                callBackStreamReadingStopped(); // tODO: This one might be obsolete now we've changed to async read calls.
            
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
                    }, cancellationToken);
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
                            await client.DisconnectAsync();
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
                    await client.Handler.ClientConnectAsync(client, cancellationToken);
                    // StartReadTask(client);
                    _ = client.ReadAsync(cancellationToken);
                }                
            });
        }

        private byte[] GenerateSelfSignedCertificate(string domain)
        {
            using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            { 
                // generate asymmetric key pair
                var req = new CertificateRequest("cn=" + domain, ecdsa, HashAlgorithmName.SHA256);
                var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(5));
                // Create PFX (PKCS #12) with private key
                return cert.Export(X509ContentType.Pfx, PfxKey);

                // Create Base 64 encoded CER (public key only)
                /*File.WriteAllText(domain + ".cer",
                    "-----BEGIN CERTIFICATE-----\r\n"
                    + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)
                    + "\r\n-----END CERTIFICATE-----");*/
            }
        }

        private void CleanUpOldClients(int noActivitySeconds = 120)
        {
            var inactiveMoment = DateTime.UtcNow.AddSeconds(-noActivitySeconds);

            List<Client>? inactiveClients = null;
            lock (Clients)
                inactiveClients = Clients.Where(c => c.LastDataReceivedMoment < inactiveMoment && c.LastDataSentMoment < inactiveMoment).ToList();

            foreach (var client in inactiveClients)
                client.DisconnectAsync().Wait(); // This stops all read tasks
        }

        /// <summary>
        /// Disposes this Server instance. This also disconnects all clients connected to this server instance.
        /// </summary>
        public void Dispose()
        {
            IsActive = false;
            lock (ActiveInstances!)
                ActiveInstances.Remove(this);

            foreach (var listenSocket in Handlers)
                listenSocket.Key.Close(); // Stop accepting new connections
            foreach (var client in Clients)
                client.DisconnectAsync().Wait(); // This stops all read tasks            
        }

        private class ServerProtocol
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
