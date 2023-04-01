using Comgenie.Server.Handlers;
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

namespace Comgenie.Server.Utils
{
    public class LetsEncryptUtil
    {
        private string LetsEncryptAPI { get; set; }
        private string AccountSettingsFile { get; set; }       
        private string AccountEmail { get; set; }
        private string AccountKeyId { get; set; }
        private RSACryptoServiceProvider AccountKey { get; set; }
        private Jwk AccountKeyMessage { get; set; }
        private HttpHandler Http { get; set; }
        private Server Server { get; set; }
        private JsonSerializerOptions JsonSettings { get; set; }
        private string Nonce { get; set; }

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
                AccountKeyId = accountSetings.KeyId;
                AccountKey.ImportCspBlob(accountSetings.RSAKey);
            }

            var publicParameters = AccountKey.ExportParameters(false);            
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

        public void CheckAndRenewAllServerDomains()
        {
            foreach (var domain in Server.Domains)
            {
                try
                {
                    if (GenerateCertificateForDomain(domain))
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
            File.WriteAllText(keyFile, JsonSerializer.Serialize(new AccountSettings()
            {
                Email = AccountEmail,
                KeyId = AccountKeyId,
                RSAKey = AccountKey.ExportCspBlob(true)
            }, JsonSettings));
        }
        public bool GenerateCertificateForDomain(string domain, bool force = false)
        {
            if (!force && File.Exists(domain + ".pfx"))
            {
                using (var certificate = new X509Certificate2(domain + ".pfx", Server.GetPfxKey()))
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

            if (!CheckConnection("http://" + domain + "/" + tempVerifyPage, verifyDomainAccessData))
            {
                Http.RemoveRoute(domain, tempVerifyPage);
                throw new Exception("Could not verify access to the domain");
            }

            Http.RemoveRoute(domain, tempVerifyPage);


            // Get nonce (needed for the Jws messages)
            if (!GetNonce())
                throw new Exception("Could not retrieve nonce");

            // Create or verify LetsEncrypt account
            // POST /acme/new-account   (Return 200 for existing account, 201 for new account created), The Location: header in the response will be used as KeyId in the Jwk messages
            var response = JwsRequest(LetsEncryptAPI + "/new-acct", new
            {
                contact = new string[] { "mailto:" + AccountEmail },
                termsOfServiceAgreed = true
            });

            if (response["type"] != null && response["type"].ToString() == "urn:ietf:params:acme:error:userActionRequired")
            {
                // TODO: Send an update that we accepted the new terms
                Log.Warning(nameof(LetsEncryptUtil), "LetsEncrypt says an user action is required: " + JsonSerializer.Serialize(response));
                return false;
            }

            if (response["status"] == null)
                throw new Exception("Invalid response after creating account " + JsonSerializer.Serialize(response));
            if (response["status"].ToString() != "valid")
                throw new Exception("Account status is: " + response["status"].ToString());

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
            string orderUrl;
            response = JwsRequest(LetsEncryptAPI + "/new-order", new
            {
                identifiers = new object[] {
                    new { type = "dns", value = domain }
                },
                //notBefore = DateTime.UtcNow.AddDays(-1).ToString("o"), // The requested value of the notBefore field in the certificate
                //notAfter = DateTime.UtcNow.AddDays(60).ToString("o") // The requested value of the notAfter field in the certificate 
            }, out orderUrl);

            // We will get a string[] back ( authorizations ), containing urls ( /acme/authz/<Identifier> ) to retrieve our challenges
            if (response["authorizations"] == null)
                throw new Exception("Error when ordering the certificate, authorizations property missing. " + JsonSerializer.Serialize(response));

            List<string> authUrls = new List<string>();
            foreach (var url in response["authorizations"].AsArray())
                authUrls.Add(url.GetValue<string>());

            // We also get a finalize url back (finalize), containing the url to call after the challenges are accepted.
            if (response["finalize"] == null)
                throw new Exception("Error when ordering the certificate, finalize property missing. " + JsonSerializer.Serialize(response));
            var finalizeUrl = response["finalize"].ToString();

            if (response["status"] == null)
                throw new Exception("Error when ordering the certificate, status property missing. " + JsonSerializer.Serialize(response));

            if (response["status"].ToString() == "pending") // We need to do the challenge(s)
            {
                // List challenges
                foreach (var authUrl in authUrls)
                {
                    // - request each Identifier Authorization challenge at:  /acme/authz/<Identifier>
                    response = JwsRequest(authUrl, null);
                    if (response["status"] == null)
                        throw new Exception("Error when requesting the authorization, status missing. " + JsonSerializer.Serialize(response));

                    if (response["status"].ToString() != "pending")
                        throw new Exception("Error when requesting the authorization, status is not pending. " + JsonSerializer.Serialize(response));

                    // - Find http challenge ( type = http-01 ) and get token
                    if (response["challenges"] == null)
                        throw new Exception("Error when requesting the authorization, challenges missing. " + JsonSerializer.Serialize(response));

                    foreach (var challengeValue in response["challenges"].AsArray())
                    {
                        if (challengeValue["type"] == null || challengeValue["url"] == null || challengeValue["token"] == null || challengeValue["type"].ToString() != "http-01")
                            continue;

                        var challengeVerifyUrl = challengeValue["url"].ToString();
                        var challengeToken = challengeValue["token"].ToString();

                        // - Host our file at: http://<YOUR_DOMAIN>/.well-known/acme-challenge/<TOKEN>
                        //   with file contents token || '.' || base64url(Thumbprint(accountKey))
                        //                      <token>.<base64HashAccountKey>
                        var fileContents = ASCIIEncoding.ASCII.GetBytes(challengeToken + "." + base64HashAccountKey);
                        Http.AddContentRoute(domain, "/.well-known/acme-challenge/" + challengeToken, fileContents, "application/octet-stream");

                        // - Test it ourself first
                        if (!CheckConnection("http://" + domain + "/.well-known/acme-challenge/" + challengeToken, fileContents))
                        {
                            Http.RemoveRoute(domain, "/.well-known/acme-challenge/" + challengeToken);
                            throw new Exception("Could not verify challenge url");
                        }

                        // - Call url with an empty {} payload to instruct the server to try ( /acme/chall/<Identifier> ) 
                        while (true)
                        {
                            var responseVerify = JwsRequest(challengeVerifyUrl, new { });

                            if (responseVerify["status"] == null)
                                throw new Exception("Error when verifying the challenge, status missing. " + JsonSerializer.Serialize(response));

                            if (responseVerify["status"].ToString() != "valid")
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
            response = JwsRequest(finalizeUrl, new
            {
                csr = CSR,
            });
            if (response["status"] == null)
                throw new Exception("Error when requesting the finalize step, status missing. " + JsonSerializer.Serialize(response));

            // Wait for 'processing' step
            while (response["status"].ToString() == "processing")
            {
                Thread.Sleep(5 * 1000);
                response = JwsRequest(orderUrl, null);
            }            
            if (response["status"].ToString() != "valid")
                throw new Exception("Error when requesting the finalize step, status is not valid. " + JsonSerializer.Serialize(response));

            // We will get a certificate string back (certificate), containing the url to download the .cer certificate ( /acme/cert/<Identifier> )
            if (response["certificate"] == null)
                throw new Exception("Certificate url is missing. " + JsonSerializer.Serialize(response));

            // Download certificate /acme/cert/<Identifier> 
            // Accept: application/pem-certificate-chain
            var certUrl = response["certificate"].ToString();
            var pemCertificateData = JwsRequestBytes(certUrl, null, out string unused);
                
            // Combine cert with certificateRequest and export an .pfx
            var cert = new X509Certificate2(pemCertificateData);
            cert = cert.CopyWithPrivateKey(certificateKey);
            File.WriteAllBytes(domain + ".pfx", cert.Export(X509ContentType.Pfx, Server.GetPfxKey()));                
            
            return true; // return true if we have a new certificate (this is used to instruct the Server to pick it up)
        }
        private bool CheckConnection(string url, byte[] contentVerify)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var data = client.GetByteArrayAsync(url).Result;
                    if (data == null || data.Length != contentVerify.Length) // todo: compare contents
                        return false;
                    return true;
                }
            }
            catch {}
            return false;
        }
        private bool GetNonce()
        {
            // HEAD /acme/new-nonce HTTP/1.1
            using (var client = new HttpClient())
            {
                var response = client.GetAsync(LetsEncryptAPI + "/new-nonce").Result;
                if (response.Headers.Contains("Replay-Nonce") && response.Headers.GetValues("Replay-Nonce").Count() > 0)
                {
                    Nonce = response.Headers.GetValues("Replay-Nonce").First();
                    return true;
                }
            }
            return false;            
        }
        private JsonObject JwsRequest(string url, object payload)
        {
            return JwsRequest(url, payload, out string unused);
        }
        private JsonObject JwsRequest(string url, object payload, out string location)
        {
            return JsonObject.Parse(ASCIIEncoding.UTF8.GetString(JwsRequestBytes(url, payload, out location))).AsObject();
        }
        private byte[] JwsRequestBytes(string url, object payload, out string location)
        {
            location = null;
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
                    var response = client.PostAsync(url, content).Result;

                    using (var dataStream = response.Content.ReadAsStream())
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
                            dataStream.CopyTo(ms);
                            return ms.ToArray();
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
            public string Email { get; set; }
            public byte[] RSAKey { get; set; } 
            public string KeyId { get; set; } 
        }

        /* JWS Message stuff */
        class Jwk
        {
            public string Kty { get; set; } // KeyType
            public string Kid { get; set; } // KeyId 
            public string Use { get; set; }
            public string N { get; set; } // Modulus
            public string E { get; set; } // Exponent
            public string D { get; set; }
            public string P { get; set; }
            public string Q { get; set; }
            public string Dp { get; set; }
            public string Dq { get; set; }
            public string Qi { get; set; } // InverseQ
            public string Alg { get; set; } // Algorithm
        }
        class JwsMessage
        {
            //public JwsMessageHeader Header { get; set; }
            public string Protected { get; set; }
            public string Payload { get; set; }
            public string Signature { get; set; }
        }
        class JwsMessageHeader
        {
            public string Alg { get; set; } // Algorithm
            public Jwk Jwk { get; set; }
            public string Kid { get; set; } // KeyId 
            public string Nonce { get; set; }
            public string Url { get; set; }
        }

    }
}
