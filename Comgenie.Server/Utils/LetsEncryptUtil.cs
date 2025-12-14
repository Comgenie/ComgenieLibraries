using Comgenie.Server.Handlers.Http;
using Comgenie.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    /// <summary>
    /// LetsEncryptUtil is a utility class to generate and renew SSL certificates using LetsEncrypt.
    /// This utility should be used with the Comgenie.Server and HttpHandler classes, as it will create temporary routes to verify domain ownership.
    /// </summary>
    public class LetsEncryptUtil
    {
        private string LetsEncryptAPI { get; set; }
        private string AccountSettingsFile { get; set; }       
        private string AccountEmail { get; set; }
        private string? AccountKeyId { get; set; }
        private RSACryptoServiceProvider AccountKey { get; set; }
        private Jwk AccountKeyMessage { get; set; }
        private HttpHandler Http { get; set; }
        private Server Server { get; set; }
        private JsonSerializerOptions JsonSettings { get; set; }
        private string? Nonce { get; set; }

        /// <summary>
        /// Creates a new LetsEncryptUtil instance. This will create a new LetsEncrypt account key if it does not exist yet, or load the existing one from the file system.
        /// Note that by using this utility, you are accepting the LetsEncrypt terms of service.
        /// </summary>
        /// <param name="server">Server instance with an httpHandler attached to it. This utility will use the pfx key set on the server instance to store the requested certificates.</param>
        /// <param name="httpHandler">Http handler which will be used to register temporary routes on</param>
        /// <param name="accountEmail">Email address which will be send to LetsEncrypt, including a flag that you've read and agreed to the LetsEncrypt terms of service.</param>
        /// <param name="useStaging">Set to true to use the LetsEncrypt testing environment</param>
        /// <exception cref="Exception">Throws an exception if the saved account settings file is corrupt</exception>
        public LetsEncryptUtil(Server server, HttpHandler httpHandler, string accountEmail, bool useStaging = false)
        {
            Http = httpHandler;
            AccountEmail = accountEmail;
            LetsEncryptAPI = useStaging ? "https://acme-staging-v02.api.letsencrypt.org/acme" : "https://acme-v02.api.letsencrypt.org/acme";
            AccountSettingsFile = "letsencrypt-" + (useStaging ? "acc" : "prd") +"-" + AccountEmail.Replace("@", "_") + ".txt";
            Server = server;
            
            AccountKey = new RSACryptoServiceProvider(4096);
            JsonSettings = new JsonSerializerOptions()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };

            // Load existing settings
            var keyFile = AccountSettingsFile;
            if (File.Exists(keyFile))
            {
                var accountSetings = JsonSerializer.Deserialize<AccountSettings>(File.ReadAllText(keyFile), JsonSettings);
                if (accountSetings?.KeyId == null || accountSetings?.RSAKey == null)
                    throw new Exception("Could not load existing letsencrypt key file");
                
                AccountKeyId = accountSetings.KeyId;
                AccountKey.ImportCspBlob(accountSetings.RSAKey);
            }

            var publicParameters = AccountKey.ExportParameters(false);
            if (publicParameters.Exponent == null || publicParameters.Modulus == null)
                throw new Exception("Missing account key parameters for letsencrypt");
            
            AccountKeyMessage = new Jwk
            {
                Kty = "RSA",
                E = Base64UrlEncoded(publicParameters.Exponent),
                N = Base64UrlEncoded(publicParameters.Modulus),
                Kid = AccountKeyId // Key Id, we will get this back from /acme/new-account  
                /*The server returns this account object in a 201 (Created) response, with the account URL in a Location header field.*/
            };

            SaveKeyFile();
        }
        /// <summary>
        /// Checks all domains currently registered within Server.Domains and renews the certificate if needed, automatically loads the certificate if it's updated.
        /// </summary>
        public void CheckAndRenewAllServerDomains() // non-async version for backwards compatibility
        {
            CheckAndRenewAllServerDomainsAsync().Wait();
        }

        /// <summary>
        /// Checks all domains currently registered within Server.Domains and renews the certificate if needed, automatically loads the certificate if it's updated.
        /// </summary>
        public async Task CheckAndRenewAllServerDomainsAsync()
        {
            foreach (var domain in Server.Domains)
            {
                try
                {
                    if (await GenerateCertificateForDomain(domain))
                        Server.AddDomain(domain); // Adding it again reloads the certificate
                }
                catch (Exception e)
                {
                    Log.Warning(nameof(LetsEncryptUtil), "Could not generate certificate for " + domain + ":" + e.Message);
                }
            }
        }

        private void SaveKeyFile()
        {
            var keyFile = AccountSettingsFile;
            File.WriteAllTextAsync(keyFile, JsonSerializer.Serialize(new AccountSettings()
            {
                Email = AccountEmail,
                KeyId = AccountKeyId,
                RSAKey = AccountKey.ExportCspBlob(true)
            }, JsonSettings));
        }

        /// <summary>
        /// Renews a specific domain certificate. If the certificate is still valid for at least 14 days, it will not be renewed unless force = true.
        /// Note that it will also check if the domain with temporary route is accessible from the server itself before requesting the certificate.
        /// </summary>
        /// <param name="domain">Domain name to register the certificate for</param>
        /// <param name="force">When set to true it will ignore the expiry date and renew a certificate even if it's still valid for more than 14 days.</param>
        /// <returns>True if a new certificate was generated, False if the existing one is still fine</returns>
        /// <exception cref="Exception">If the domain was not accessable from this server, or if the renew failed for any reason an exception will be thrown</exception>
        public async Task<bool> GenerateCertificateForDomain(string domain, bool force = false)
        {
            var certificatePath = Path.Combine(GlobalConfiguration.SecretsFolder, domain + ".pfx");

            if (!force && File.Exists(certificatePath))
            {
                using (var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, Server.PfxKey))
                {
                    // Check if certificate we have is still valid for at least 14 days, if so: we don't have to do anything (unless force = true)                    
                    if (certificate.Issuer.Contains("Let's Encrypt") && DateTime.UtcNow.AddDays(14) < certificate.NotAfter) 
                        return false;                    
                }
            }

            // Check if we can access the domain ourself            
            var tempVerifyPage = "LetsEncryptDomainVerifyPage" + Guid.NewGuid();
            byte[] verifyDomainAccessData = new byte[] { 42 };
            Http.AddContentRoute(domain, tempVerifyPage, verifyDomainAccessData, "text/plain");

            if (!await CheckConnectionAsync("http://" + domain + "/" + tempVerifyPage, verifyDomainAccessData))
            {
                Http.RemoveRoute(domain, tempVerifyPage);
                throw new Exception("Could not verify access to the domain");
            }

            Http.RemoveRoute(domain, tempVerifyPage);


            // Get nonce (needed for the Jws messages)
            if (!await GetNonceAsync() || Nonce == null)
                throw new Exception("Could not retrieve nonce");

            // Create or verify LetsEncrypt account
            // POST /acme/new-account   (Return 200 for existing account, 201 for new account created), The Location: header in the response will be used as KeyId in the Jwk messages
            var response = await JwsRequestSimpleAsync(LetsEncryptAPI + "/new-acct", new
            {
                contact = new string[] { "mailto:" + AccountEmail },
                termsOfServiceAgreed = true
            });

            if (response != null && response["type"] != null && response["type"]?.ToString() == "urn:ietf:params:acme:error:userActionRequired")
            {
                // TODO: Send an update that we accepted the new terms
                Log.Warning(nameof(LetsEncryptUtil), "LetsEncrypt says an user action is required: " + JsonSerializer.Serialize(response));
                return false;
            }

            if (response == null || response["status"] == null)
                throw new Exception("Invalid response after creating account " + JsonSerializer.Serialize(response));

            if (response["status"]?.ToString() != "valid")
                throw new Exception("Account status is: " + response["status"]?.ToString());

            SaveKeyFile(); // If we haven't had an AccountKeyId before, we have one now           

            // Generate account key hash (needed for challenge)
            var base64HashAccountKey = "{\"e\":\"" + AccountKeyMessage.E + "\",\"kty\":\"RSA\",\"n\":\"" + AccountKeyMessage.N + "\"}";
            using (var sha256 = SHA256.Create())
                base64HashAccountKey = Base64UrlEncoded(sha256.ComputeHash(Encoding.UTF8.GetBytes(base64HashAccountKey)));

            // Create certificate request
            var certificateKey = new RSACryptoServiceProvider(4096);
            var certificateRequest = new CertificateRequest("cn=" + domain, certificateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(domain);
            certificateRequest.CertificateExtensions.Add(san.Build());
            var CSR = Base64UrlEncoded(certificateRequest.CreateSigningRequest());

            // Order certificate for domain /acme/new-order
            string? orderUrl;
            var responseWithLocation = await JwsRequestAsync(LetsEncryptAPI + "/new-order", new
            {
                identifiers = new object[] {
                    new { type = "dns", value = domain }
                },
                //notBefore = DateTime.UtcNow.AddDays(-1).ToString("o"), // The requested value of the notBefore field in the certificate
                //notAfter = DateTime.UtcNow.AddDays(60).ToString("o") // The requested value of the notAfter field in the certificate 
            });
            response = responseWithLocation.jsonObj;

            if (responseWithLocation.location == null)
                throw new Exception("Missing order url in letsencrypt response");

            // We will get a string[] back ( authorizations ), containing urls ( /acme/authz/<Identifier> ) to retrieve our challenges
            if (response == null || response["authorizations"] == null)
                throw new Exception("Error when ordering the certificate, authorizations property missing. " + JsonSerializer.Serialize(response));
            
            List<string> authUrls = new List<string>();
            foreach (var url in response["authorizations"]!.AsArray())
                authUrls.Add(url!.GetValue<string>());
            
            // We also get a finalize url back (finalize), containing the url to call after the challenges are accepted.
            if (response["finalize"] == null)
                throw new Exception("Error when ordering the certificate, finalize property missing. " + JsonSerializer.Serialize(response));
            var finalizeUrl = response["finalize"]!.ToString();

            if (response["status"] == null)
                throw new Exception("Error when ordering the certificate, status property missing. " + JsonSerializer.Serialize(response));

            if (response["status"]!.ToString() == "pending") // We need to do the challenge(s)
            {
                // List challenges
                foreach (var authUrl in authUrls)
                {
                    // - request each Identifier Authorization challenge at:  /acme/authz/<Identifier>
                    response = await JwsRequestSimpleAsync(authUrl, null);
                    if (response == null)
                        continue;

                    if (response["status"] == null)
                        throw new Exception("Error when requesting the authorization, status missing. " + JsonSerializer.Serialize(response));

                    if (response["status"]!.ToString() != "pending")
                        throw new Exception("Error when requesting the authorization, status is not pending. " + JsonSerializer.Serialize(response));

                    // - Find http challenge ( type = http-01 ) and get token
                    if (response["challenges"] == null)
                        throw new Exception("Error when requesting the authorization, challenges missing. " + JsonSerializer.Serialize(response));

                    foreach (var challengeValue in response["challenges"]!.AsArray())
                    {
                        if (challengeValue == null || challengeValue["type"] == null || challengeValue["url"] == null || challengeValue["token"] == null || challengeValue["type"]!.ToString() != "http-01")
                            continue;

                        var challengeVerifyUrl = challengeValue["url"]!.ToString();
                        var challengeToken = challengeValue["token"]!.ToString();

                        // - Host our file at: http://<YOUR_DOMAIN>/.well-known/acme-challenge/<TOKEN>
                        //   with file contents token || '.' || base64url(Thumbprint(accountKey))
                        //                      <token>.<base64HashAccountKey>
                        var fileContents = ASCIIEncoding.ASCII.GetBytes(challengeToken + "." + base64HashAccountKey);
                        Http.AddContentRoute(domain, "/.well-known/acme-challenge/" + challengeToken, fileContents, "application/octet-stream");

                        // - Test it ourself first
                        if (!await CheckConnectionAsync("http://" + domain + "/.well-known/acme-challenge/" + challengeToken, fileContents))
                        {
                            Http.RemoveRoute(domain, "/.well-known/acme-challenge/" + challengeToken);
                            throw new Exception("Could not verify challenge url");
                        }

                        // - Call url with an empty {} payload to instruct the server to try ( /acme/chall/<Identifier> ) 
                        while (true)
                        {
                            var responseVerify = await JwsRequestSimpleAsync(challengeVerifyUrl, new { });
                            if (responseVerify == null)
                            {
                                Thread.Sleep(5 * 1000); // Retry in 5 sec
                                continue;
                            }

                            if (responseVerify["status"] == null)
                                throw new Exception("Error when verifying the challenge, status missing. " + JsonSerializer.Serialize(response));

                            if (responseVerify["status"]!.ToString() != "valid")
                            {
                                Thread.Sleep(5 * 1000); // Retry in 5 sec
                                continue;
                            }
                            break;
                        }

                        // - Clean up
                        Http.RemoveRoute(domain, "/.well-known/acme-challenge/" + challengeToken);

                        break; // Only have to do 1 challenge
                    }
                }

                // Call finalize step with our CSR     
            }
            response = await JwsRequestSimpleAsync(finalizeUrl, new
            {
                csr = CSR,
            });
            if (response == null)
                throw new Exception("Did not get a valid JwsRequest response");

            if (response["status"] == null)
                throw new Exception("Error when requesting the finalize step, status missing. " + JsonSerializer.Serialize(response));

            // Wait for 'processing' step
            while (response["status"]!.ToString() == "processing")
            {
                Thread.Sleep(5 * 1000);
                response = await JwsRequestSimpleAsync(responseWithLocation.location, null);
                if (response == null || response["status"] == null)
                    throw new Exception("Did not get a valid JwsRequest response");
            }            

            if (response["status"]!.ToString() != "valid")
                throw new Exception("Error when requesting the finalize step, status is not valid. " + JsonSerializer.Serialize(response));

            // We will get a certificate string back (certificate), containing the url to download the .cer certificate ( /acme/cert/<Identifier> )
            if (response["certificate"] == null)
                throw new Exception("Certificate url is missing. " + JsonSerializer.Serialize(response));

            // Download certificate /acme/cert/<Identifier> 
            // Accept: application/pem-certificate-chain
            var certUrl = response["certificate"]!.ToString();
            var pemCertificateData = await JwsRequestBytesAsync(certUrl, null);
                
            // Combine cert with certificateRequest and export an .pfx
            var cert = X509CertificateLoader.LoadCertificate(pemCertificateData.bytes);
            cert = cert.CopyWithPrivateKey(certificateKey);

            

            File.WriteAllBytes(certificatePath, cert.Export(X509ContentType.Pkcs12, Server.PfxKey));                
            
            return true; // return true if we have a new certificate (this is used to instruct the Server to pick it up)
        }
        private async Task<bool> CheckConnectionAsync(string url, byte[] contentVerify)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var data = await client.GetByteArrayAsync(url);
                    if (data == null || data.Length != contentVerify.Length) // todo: compare contents
                        return false;
                    return true;
                }
            }
            catch {}
            return false;
        }
        private async Task<bool> GetNonceAsync()
        {
            // HEAD /acme/new-nonce HTTP/1.1
            using (var client = new HttpClient())
            {
                var response = await client.GetAsync(LetsEncryptAPI + "/new-nonce");
                if (response.Headers.Contains("Replay-Nonce") && response.Headers.GetValues("Replay-Nonce").Count() > 0)
                {
                    Nonce = response.Headers.GetValues("Replay-Nonce").First();
                    return true;
                }
            }
            return false;            
        }
        private async Task<JsonObject?> JwsRequestSimpleAsync(string url, object? payload)
        {
            var response = await JwsRequestAsync(url, payload);
            return response.jsonObj;
        }
        private async Task<(JsonObject? jsonObj, string? location)> JwsRequestAsync(string url, object? payload)
        {
            var response = await JwsRequestBytesAsync(url, payload);
            var jsonObj = JsonObject.Parse(ASCIIEncoding.UTF8.GetString(response.bytes))?.AsObject();
            return (jsonObj, response.location);
        }
        private async Task<(byte[] bytes, string? location)> JwsRequestBytesAsync(string url, object? payload)
        {
            string? location = null;
            var JwkRequired = url.Contains("new-acct") || url.Contains("revoke");
            var CertDownload = url.Contains("/cert/");

            var header = new JwsMessageHeader()
            {
                Alg = "RS256",
                Kid = JwkRequired ? null : AccountKeyId,
                Jwk = JwkRequired ? AccountKeyMessage : null,
                Nonce = Nonce,
                Url = url
            };
            
            var message = new JwsMessage()
            {
                Payload = payload == null ? "" : Base64UrlEncoded(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonSettings))),
                Protected = Base64UrlEncoded(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header, JsonSettings)))
            };
            message.Signature = Base64UrlEncoded(AccountKey.SignData(Encoding.ASCII.GetBytes(message.Protected + "." + message.Payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            var data = JsonSerializer.Serialize(message, JsonSettings);

            using (var client = new HttpClient())
            {
                if (CertDownload)
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pem-certificate-chain"));

                // Get response
                try
                {
                    var content = new StringContent(data, Encoding.UTF8, "application/jose+json");

                    // Override the content type as LetsEncrypt does not like the charset to be included
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
                    var response = await client.PostAsync(url, content);

                    using (var dataStream = await response.Content.ReadAsStreamAsync())
                    {
                        if (response.Headers.Contains("Replay-Nonce"))
                            Nonce = response.Headers.GetValues("Replay-Nonce").First();
                        if (response.Headers.Contains("Location"))
                        {
                            location = response.Headers.GetValues("Location").First();
                            if (url.Contains("new-acct")) // The Location: header in the new account creation message will contain the KeyId)
                            {
                                AccountKeyMessage.Kid = location;
                                AccountKeyId = location;
                            }
                        }

                        using (var ms = new MemoryStream())
                        {
                            await dataStream.CopyToAsync(ms);
                            return (ms.ToArray(), location);
                        }
                    }
                }
                catch (HttpRequestException ex)
                {
                    Log.Warning(nameof(LetsEncryptUtil), "Error when posting data: " + ex.Message);
                    throw;
                }
            }            
        }

        private static string Base64UrlEncoded(byte[] arg)
        {
            return Convert.ToBase64String(arg).Split('=')[0].Replace('+', '-').Replace('/', '_');
        }

        class AccountSettings
        {
            public string? Email { get; set; }
            public byte[]? RSAKey { get; set; } 
            public string? KeyId { get; set; } 
        }

        /* JWS Message stuff */
        class Jwk
        {
            public string? Kty { get; set; } // KeyType
            public string? Kid { get; set; } // KeyId 
            public string? Use { get; set; }
            public string? N { get; set; } // Modulus
            public string? E { get; set; } // Exponent
            public string? D { get; set; }
            public string? P { get; set; }
            public string? Q { get; set; }
            public string? Dp { get; set; }
            public string? Dq { get; set; }
            public string? Qi { get; set; } // InverseQ
            public string? Alg { get; set; } // Algorithm
        }
        class JwsMessage
        {
            //public JwsMessageHeader Header { get; set; }
            public string? Protected { get; set; }
            public string? Payload { get; set; }
            public string? Signature { get; set; }
        }
        class JwsMessageHeader
        {
            public string? Alg { get; set; } // Algorithm
            public Jwk? Jwk { get; set; }
            public string? Kid { get; set; } // KeyId 
            public string? Nonce { get; set; }
            public string? Url { get; set; }
        }

    }
}
