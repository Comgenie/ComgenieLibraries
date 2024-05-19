using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    public class SmtpUtil
    {
        private static Dictionary<string, RSACryptoServiceProvider?> DkimRsa = new Dictionary<string, RSACryptoServiceProvider?>();
        public static RSACryptoServiceProvider? GetDkimRsa(string domain, bool createIfNotExists=false, bool checkIfCorrectlyAddedToDomain=true)
        {
            if (!DkimRsa.ContainsKey(domain))
            {
                DkimRsa.Add(domain, null);
                // Check if there is a dkim key pair
                if (!File.Exists("dkim-"+domain+".key"))
                {
                    if (!createIfNotExists)
                        return null;
                    //Create a new instance of RSACryptoServiceProvider to generate
                    //public and private key data.
                    using (var rsaNew = new RSACryptoServiceProvider(2048))
                    {
                        if (File.Exists("dkim-" + domain + ".pem"))
                            rsaNew.ImportFromPem(File.ReadAllText("dkim-" + domain + ".pem"));

                        File.WriteAllBytes("dkim-" + domain + ".key", rsaNew.ExportCspBlob(true));
                        File.WriteAllText("instruction-dns-for-" + domain + ".txt", "Create a DNS record with the following settings: \r\nName: dkim._domainkey\r\nTTL: 1 hour\r\nType: TXT\r\nValue: v=DKIM1;t=s;k=rsa;p=" + SmtpUtil.ExportPublicKey(rsaNew));
                    }
                } 

                var rsa = new RSACryptoServiceProvider(2048);
                rsa.ImportCspBlob(File.ReadAllBytes("dkim-"+domain+".key"));

                // Check if the DKIM TXT record is added to the domain                
                if (checkIfCorrectlyAddedToDomain)
                {
                    var pubKey = SmtpUtil.ExportPublicKey(rsa);
                    var currentTXTRecord = GetDNSResult("dkim._domainkey." + domain, "TXT").Result;
                    if (currentTXTRecord == null || currentTXTRecord.Length == 0)
                    {
                        Console.WriteLine("Warning: DKIM TXT Record for " + domain + " missing.");
                    }
                    else if (!currentTXTRecord.Any(a => a.Contains(pubKey)))
                    {
                        Console.WriteLine("Warning: DKIM TXT Record for " + domain + " is not having the correct key.");
                    }
                }

                DkimRsa[domain] = rsa;
            }

            return DkimRsa[domain];
        }

        private static string SplitInto79CharLines(string data)
        {
            StringBuilder sb = new StringBuilder();

            var i = 0;
            while (i < data.Length)
            {
                int charsThisLine = 79;
                if (data[i] == '.') // If a line starts with a dot, we will add another dot
                {
                    sb.Append(".");
                    charsThisLine--; // The max length still can't exceed 79 characters
                }

                if (i + charsThisLine >= data.Length) // End
                {
                    sb.Append(data.Substring(i));
                    break;
                }

                var line = data.Substring(i, charsThisLine);
                
                var posEnter = line.IndexOf("\r\n"); // There is already an enter at this line
                if (posEnter >= 0)
                {
                    sb.Append(line.Substring(0, posEnter + 2));
                    i = i + posEnter + 2;
                    continue;
                }

                sb.Append(line);
                sb.Append("\r\n");
                i += charsThisLine;
            }

            return sb.ToString();
        }

        public static void QueueSendMail(MailMessage message)
        {
            // TODO: Better queue system
            _ = SendMail(message);
        }

        public static string? GetDKIMSignature(string fromDomain, Stream emlData, string includeHeaders = "From,To,Cc,Subject,Content-Type,Content-Transfer-Encoding,Message-ID,Date")
        {
            if (emlData == null)
                throw new ArgumentException();
            var hash = SHA256.Create();
            if (hash == null)
            {
                Console.WriteLine("Could not initialize SHA-256 hasher");
                return null;
            }

            CanonicalizatedBodyStream.ForwardStreamPositionToBody(emlData);

            string bodyHash = Convert.ToBase64String(hash.ComputeHash(new CanonicalizatedBodyStream(emlData, true)));

            var dkimRsa = GetDkimRsa(fromDomain);
            if (dkimRsa == null)
            {
                Console.WriteLine("Warning: No DKIM for domain " + fromDomain + " found.");
                return null; // There is no dkim signature for this domain
            }

            var emailDate = DateTime.UtcNow;
            var headerFields = includeHeaders.Split(',');
            string messageId = "<" + Guid.NewGuid().ToString() + "@" + fromDomain + ">";
            bool addOwnMessageId = false;

            var canonicalizedHeaders = "";
            var headerFieldsSignature = "";

            Dictionary<string, int> headerOffsetCount = new Dictionary<string, int>();
            foreach (var headerField in headerFields)
            {
                var headerLines = SmtpUtil.GetHeaderValues(emlData, headerField, includeFullLine: true, canonicalizated: true, onlyIncludeFirst: false);

                if (headerField == "Message-ID" && headerLines.Count == 0)
                {
                    headerLines = new List<string>() { "message-id:" + messageId };
                    addOwnMessageId = true;
                }

                // Make sure to always pick the last one we haven't had. Ignore if we don't have more of the headers
                if (!headerOffsetCount.ContainsKey(headerField))
                    headerOffsetCount.Add(headerField, 0);
                if (headerLines.Count <= headerOffsetCount[headerField])
                    continue; // ignore missing headers
                var headerLine = headerLines.Skip(headerLines.Count - (1 + headerOffsetCount[headerField])).First();
                headerOffsetCount[headerField]++;

                if (canonicalizedHeaders.Length > 0)
                    canonicalizedHeaders += "\r\n";
                canonicalizedHeaders += headerLine;
                headerFieldsSignature += ((headerFieldsSignature.Length == 0) ? "" : ":") + headerField;
            }

            var epoch = Convert.ToInt64((emailDate - DateTime.SpecifyKind(DateTime.Parse("00:00:00 January 1, 1970"), DateTimeKind.Utc)).TotalSeconds);
            string signatureHeader = "v=1; " +
             "a=rsa-sha256; " +
             "c=relaxed/simple; " +
             "d=" + fromDomain + "; " +
             "s=dkim; " +
             "q=dns/txt; " +
             "t=" + epoch + "; " +
             "bh=" + bodyHash + "; " + // Hashout is a sha256 hash of the email body
             "h="+headerFieldsSignature+"; " +
             "b=";

            canonicalizedHeaders += "\r\ndkim-signature:" + signatureHeader;

            var signature = dkimRsa.SignData(ASCIIEncoding.ASCII.GetBytes(canonicalizedHeaders), SHA256.Create());
            signatureHeader += Convert.ToBase64String(signature);

            return "DKIM-Signature: " + signatureHeader + "\r\n" 
                + (addOwnMessageId ? ("Message-ID: " + messageId + "\r\n") : "");
        }

        public static async Task<string> SendMail(MailMessage message, bool ignoreCertificateIssues = false)
        {
            if (message.From == null)
                throw new ArgumentException("Email message must have a from address set");
            var fromDomain = message.From.Host;
            string messageId = "<" + Guid.NewGuid().ToString() + "@" + fromDomain + ">";
            var emailDate = DateTime.UtcNow;
            string date = emailDate.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000";

            // Generate body            
            var subject = message.Subject;
            bool base64fullBody = true;

            string contentType = message.IsBodyHtml ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";            
            var body = message.Body;
            
            if (message.AlternateViews.Count > 0)
            {                
                // Mail is multipart/alternative
                var boundary = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 16);

                StringBuilder newBody = new StringBuilder();
                newBody.Append("--" + boundary + "\r\n");
                newBody.Append("Content-Type: " + contentType + "\r\n");
                newBody.Append("Content-Transfer-Encoding: base64\r\n\r\n");                
                newBody.Append(Convert.ToBase64String(UTF8Encoding.UTF8.GetBytes(body)));
                
                foreach (var view in message.AlternateViews)
                {
                    var tempMs = new MemoryStream();
                    view.ContentStream.CopyTo(tempMs);

                    newBody.Append("\r\n--" + boundary + "\r\n");
                    newBody.Append("Content-Type: "+ view.ContentType + "\r\n");
                    newBody.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                    newBody.Append(Convert.ToBase64String(tempMs.ToArray()));
                }

                newBody.Append("\r\n--" + boundary + "--\r\n");

                body = newBody.ToString();

                base64fullBody = false;
                contentType = "multipart/alternative; boundary=\"" + boundary + "\"";
            }

            if (message.Attachments.Count > 0)
            {                
                // Mail is (also) multipart/mixed
                var boundary = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 16);

                StringBuilder newBody = new StringBuilder();
                newBody.Append("--" + boundary + "\r\nContent-Type: " + contentType + "\r\n\r\n" + body);

                foreach (var attachment in message.Attachments)
                {
                    var tempMs = new MemoryStream();
                    attachment.ContentStream.CopyTo(tempMs);

                    newBody.Append("\r\n--" + boundary + "\r\n");
                    newBody.Append("Content-Type: " + attachment.ContentType + "\r\n");
                    if (attachment.ContentDisposition != null)
                        newBody.Append("Content-Disposition: "+ attachment.ContentDisposition.ToString() + "\r\n");
                    newBody.Append("Content-Transfer-Encoding: base64\r\n");

                    if (!string.IsNullOrEmpty(attachment.ContentId))
                    {
                        newBody.Append("Content-ID: <" + attachment.ContentId + ">\r\n");
                        newBody.Append("X-Attachment-Id: " + attachment.ContentId + "\r\n");
                    }
                    newBody.Append("\r\n");
                    newBody.Append(Convert.ToBase64String(tempMs.ToArray()));
                }
                newBody.Append("\r\n--" + boundary + "--\r\n");

                body = newBody.ToString();
                base64fullBody = false;
                contentType = "multipart/mixed; boundary=\"" + boundary + "\"";
            }

            HashAlgorithm? hash = SHA256.Create();
            if (hash == null)
                throw new Exception("SHA-256 hasher could not be initialized");
            if (base64fullBody)
                body = Convert.ToBase64String(UTF8Encoding.UTF8.GetBytes(body));
            var bodyWithEnters = SplitInto79CharLines(body);
            if (!bodyWithEnters.EndsWith("\r\n"))
                bodyWithEnters += "\r\n"; // Our email body should always end with an enter

            byte[] bodyBytes = Encoding.ASCII.GetBytes(bodyWithEnters); // at this point the content should always be ASCII, any UTF-8 content is base64 encoded
            string hashout = Convert.ToBase64String(hash.ComputeHash(bodyBytes));

            // Generate signature
            var dkimRsa = GetDkimRsa(fromDomain);
            if (dkimRsa == null)
                throw new Exception("There is no DKIM key pair for domain " + fromDomain);

            var epoch = Convert.ToInt64((emailDate - DateTime.SpecifyKind(DateTime.Parse("00:00:00 January 1, 1970"), DateTimeKind.Utc)).TotalSeconds);
            string signatureHeader = "v=1; " +
             "a=rsa-sha256; " +
             "c=relaxed/simple; " +
             "d="+fromDomain+"; " +
             "s=dkim; " +
             "q=dns/txt; " +
             "t=" + epoch + "; " +
             "bh=" + hashout + "; " + // Hashout is a sha256 hash of the email body
             "h=From:To:" + (message.CC.Count > 0 ? "Cc:" : "") + "Subject:Content-Type:"+(base64fullBody ? "Content-Transfer-Encoding:" : "") + "Message-ID:Date; " +
             "b=";

            string canonicalizedHeaders = "from:" + message.From.ToString() + "\r\n" +
                "to:" + message.To.ToString() + "\r\n" +
                (message.CC.Count > 0 ? "cc:" + message.CC.ToString() + "\r\n" : "") +
                "subject:" + subject + "\r\n" +
                "content-type:" + contentType + "\r\n" +
                (base64fullBody ? "content-transfer-encoding:base64\r\n" : "") +
                "message-id:" + messageId + "\r\n" +
                "date:" + date + "\r\n" +
                "dkim-signature:" + signatureHeader;

            var signature = dkimRsa.SignData(ASCIIEncoding.ASCII.GetBytes(canonicalizedHeaders), SHA256.Create());
            signatureHeader += Convert.ToBase64String(signature);

            // Generate headers and combine with body
            StringBuilder emailData = new StringBuilder();
            emailData.Append("DKIM-Signature: " + signatureHeader + "\r\n");
            emailData.Append("From: " + message.From.ToString() + "\r\n");
            emailData.Append("To: " + message.To.ToString() + "\r\n");
            if (message.CC.Count > 0)
                emailData.Append("Cc: " + message.CC.ToString() + "\r\n");
            emailData.Append("Subject: " + subject + "\r\n");
            emailData.Append("Content-Type: " + contentType + "\r\n");
            if (base64fullBody)
                emailData.Append("Content-Transfer-Encoding: base64\r\n");
            emailData.Append("Message-ID: " + messageId + "\r\n");
            emailData.Append("Date: " + date + "\r\n");
            emailData.Append("\r\n");
            emailData.Append(bodyWithEnters);
            var toTest = message.To.ToString();

            // Send message to the MX servers
            var receiverAddresses = new HashSet<string>();
            receiverAddresses.UnionWith(message.To.Select(a => a.Address));
            receiverAddresses.UnionWith(message.CC.Select(a => a.Address));
            receiverAddresses.UnionWith(message.Bcc.Select(a => a.Address));

            var ms = new MemoryStream(ASCIIEncoding.ASCII.GetBytes(emailData.ToString()));
            await SendEmailRaw(message.From.Address, receiverAddresses.ToArray(), ms, ignoreCertificateIssues: ignoreCertificateIssues);
            return emailData.ToString();
        }

        public static string? GetAddressDomain(string? address)
        {
            address = GetMailAddress(address);
            if (address == null)
                return null;            
            return address.Substring(address.IndexOf("@") + 1).ToLower();
        }
        public static string? GetMailAddress(string? mailAddressWithNameAndExtras)
        {
            if (string.IsNullOrEmpty(mailAddressWithNameAndExtras))
                return null;

            mailAddressWithNameAndExtras = mailAddressWithNameAndExtras.ToLower().Trim();
            int start = mailAddressWithNameAndExtras.IndexOf("<");
            int end = mailAddressWithNameAndExtras.IndexOf(">");
            if (start >= 0 && end > start)
                mailAddressWithNameAndExtras = mailAddressWithNameAndExtras.Substring(start + 1, end - start - 1);

            if (!mailAddressWithNameAndExtras.Contains("@"))
                return null;

            return mailAddressWithNameAndExtras;
        }

        private static bool AcceptAllCertificates(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // There is this fun technique called 'DANE' to offer certificates using a different subject name by using special TLSA dns records
            // Since we do not support it yet, we will have an ugly option to just disable the certificate check to make sending mails work
            // Thanks for the added security..
            return true;
        }

        public static async Task SendEmailRaw(string mailFrom, string[] rcptTo, Stream emailData, bool addExtraDotToBeginningOfLinesWithDots = false, string? addHeadersBeforeEmailData = null, bool ignoreCertificateIssues = false)
        {
            var groups = rcptTo.GroupBy(a => GetAddressDomain(a));
            foreach (var group in groups)
            {
                if (group.Key == null)
                    continue;
                emailData.Position = 0;
                await SendEmailRaw(group.Key, mailFrom, group.ToArray(), emailData, addExtraDotToBeginningOfLinesWithDots, addHeadersBeforeEmailData, ignoreCertificateIssues: ignoreCertificateIssues);
            }
        }

        public static async Task SendEmailRaw(string receiverDomain, string mailFrom, string[] rcptTo, Stream emailData, bool addExtraDotToBeginningOfLinesWithDots=false, string? addHeadersBeforeEmailData=null, string? customEhlo=null, bool ignoreCertificateIssues=false)
        {
            var fromDomain = GetAddressDomain(mailFrom);
            if (rcptTo.Length == 0)
                return;
            
            var mxServer = await GetDNSResult(receiverDomain, "MX");
            if (mxServer == null || mxServer.Length == 0)
            {
                // TODO: Send later
                throw new Exception("Could not find the mx server for " + receiverDomain);
            }

            using (TcpClient tcp = new TcpClient(AddressFamily.InterNetwork))
            {
                await tcp.ConnectAsync(mxServer[0], 25);

                using (var stream = tcp.GetStream())
                using (var sslStream = new SslStream(stream, false, ignoreCertificateIssues ? new RemoteCertificateValidationCallback(AcceptAllCertificates) : null))
                {
                    var writer = new StreamWriter(stream, ASCIIEncoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                    var reader = new StreamReader(stream, ASCIIEncoding.ASCII);

                    var banner = await ReadAll(reader, "220");
                    Console.WriteLine("SMTP Banner: " + banner);

                    await writer.WriteLineAsync("EHLO " + (string.IsNullOrEmpty(customEhlo) ? fromDomain : customEhlo));
                    var ehlo = await ReadAll(reader, "250");
                    if (ehlo.Contains("STARTTLS"))
                    {
                        // Enable SSL
                        await writer.WriteLineAsync("STARTTLS");
                        await ReadAll(reader, "220");

                        await sslStream.AuthenticateAsClientAsync(mxServer[0]);

                        writer = new StreamWriter(sslStream, ASCIIEncoding.ASCII) { AutoFlush = true }; // , NewLine = "\r\n"
                        reader = new StreamReader(sslStream, ASCIIEncoding.ASCII);

                        // Say ehlo again after TLS
                        await writer.WriteLineAsync("EHLO " + (string.IsNullOrEmpty(customEhlo) ? fromDomain : customEhlo));
                        ehlo = await ReadAll(reader, "250");
                    }

                    await writer.WriteLineAsync("MAIL FROM: " + (mailFrom.Contains("<") ? mailFrom : "<" + mailFrom +">"));
                    await ReadAll(reader, "250");

                    foreach (var to in rcptTo)
                    {
                        await writer.WriteLineAsync("RCPT TO: " + (to.Contains("<") ? to : "<" + to + ">"));
                        await ReadAll(reader, "250");
                    }

                    await writer.WriteLineAsync("DATA");
                    await ReadAll(reader, "354");

                    emailData.Position = 0;
                    using (var emailDataReader = new StreamReader(emailData, ASCIIEncoding.ASCII))
                    {
                        writer.AutoFlush = false;

                        if (addHeadersBeforeEmailData != null)
                            await writer.WriteAsync(addHeadersBeforeEmailData);

                        // TODO: Make it streaming using ReadBlock. We should not use ReadLine as we need to preserve all enters exactly  (for dkim)
                        
                        // Retrieve and send as lines
                        string? readLine = null;
                        while ((readLine = await emailDataReader.ReadLineAsync()) != null) // The SMTP protocol is very text based
                        {
                            if (!tcp.Connected)
                                throw new Exception("Connection lost while sending stream");

                            if (addExtraDotToBeginningOfLinesWithDots && readLine.StartsWith(".")) // The code executing this method indicates that there are no extra dots added yet
                                readLine = "." + readLine;

                            await writer.WriteLineAsync(readLine);                                                        
                        }
                        
                        await writer.FlushAsync();
                        writer.AutoFlush = true;
                    }

                    await writer.WriteLineAsync(".");
                    await ReadAll(reader, "250");

                    await writer.WriteLineAsync("QUIT");

                    writer.Close();
                    reader.Close();
                }
            }
        }

        private static async Task<string> ReadAll(StreamReader reader, string? checkIfLineStartsWith=null)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync();
                sb.AppendLine(line);
                Console.WriteLine("SMTP: " + line);
                if (line == null)
                    throw new Exception("SMTP connection closed");
                if (checkIfLineStartsWith != null && !line.StartsWith(checkIfLineStartsWith))
                    throw new Exception("Unexpected SMTP response: " + line);

                if (line.Length < 4 || line[3] == ' ')
                    return sb.ToString();
            }
        }

        private static Dictionary<string, Tuple<DateTime, string[]?>> DnsCache = new Dictionary<string, Tuple<DateTime, string[]?>>();


        public static async Task<string[]?> GetDNSResult(string domain, string type)
        {
            var cacheKey = domain + "|" + type;
            lock (DnsCache)
            {
                if (DnsCache.ContainsKey(cacheKey) && DnsCache[cacheKey].Item1 > DateTime.UtcNow)
                    return DnsCache[cacheKey].Item2;
            }

            string[]? result = null;

            var MXData = new Regex("data\": ?\"(.+?)\"", RegexOptions.Compiled);
            MatchCollection? matches = null;
            for (var tries = 0; tries < 3; tries++)
            {
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                    using (var client = new HttpClient())
                    {
                        var data = await client.GetStringAsync("https://dns.google.com/resolve?name=" + domain + "&type="+type);
                        matches = MXData.Matches(data);
                    }
                    break;
                }
                catch (Exception)
                {
                    if (tries == 2)
                        break;
                    await Task.Delay(2000);
                }
            }

            if (matches != null && matches.Count > 0)
            {
                if (type == "MX")
                {
                    List<Tuple<int, string>> mxRecordsFound = new List<Tuple<int, string>>();
                    foreach (Match match in matches)
                    {
                        var parts = match.Groups[1].Value.Split(' ');
                        mxRecordsFound.Add(new Tuple<int, string>(Int32.Parse(parts[0]), parts[1]));
                    }
                    result = mxRecordsFound.OrderBy(a => a.Item1).Select(a => (a.Item2.EndsWith(".") ? a.Item2.Substring(0, a.Item2.Length - 1) : a.Item2)).ToArray();
                }
                else
                {
                    result = matches.Select(a=>a.Groups[1].Value).ToArray();
                }
            }

            lock (DnsCache)
            {
                if (DnsCache.ContainsKey(cacheKey))
                    DnsCache[cacheKey] = new Tuple<DateTime, string[]?>(DateTime.UtcNow.AddMinutes(30), result);
                else
                    DnsCache.Add(cacheKey, new Tuple<DateTime, string[]?>(DateTime.UtcNow.AddMinutes(30), result));
            }

            return result;

        }
        public static bool CheckSPF(string ip)
        {
            return false;
        }
        public static async Task<string?> CheckDKIM(Stream file) // returns signature domain
        {
            file.Position = 0;
            var dkim = SmtpUtil.GetHeaderValue(file, "DKIM-Signature");
            if (string.IsNullOrEmpty(dkim))
                return null;

            var dkimSignatureParts = dkim.Split(';')
                .Select(a => a.Trim().Replace("\t","").Split('=', 2))
                .Where(a=>a.Length == 2)
                .ToDictionary(a => a[0], a => a[1]);

            if (!dkimSignatureParts.ContainsKey("b"))
                throw new Exception("DKIM-Signature actual signature is missing");

            if (dkimSignatureParts.ContainsKey("t")) // Timestamp, we can check if this is not too old
            {
                var epoch = long.Parse(dkimSignatureParts["t"]);
                var expireEpoch = Convert.ToInt64((DateTime.UtcNow.AddDays(7) - DateTime.SpecifyKind(DateTime.Parse("00:00:00 January 1, 1970"), DateTimeKind.Utc)).TotalSeconds);
                if (dkimSignatureParts.ContainsKey("x")) // Contains a custom expiry date
                    expireEpoch = long.Parse(dkimSignatureParts["x"]);
                if (epoch > expireEpoch)
                    throw new Exception("DKIM-Signature is expired");                
            }

            // Find Canonicalization settings
            var canonicalizationHeader = "simple";
            var canonicalizationBody = "simple";
            if (dkimSignatureParts.ContainsKey("c"))
            {
                if (dkimSignatureParts["c"].Contains("/"))
                {
                    canonicalizationHeader = dkimSignatureParts["c"].Split('/')[0];
                    canonicalizationBody = dkimSignatureParts["c"].Split('/')[1];

                }
                else
                {
                    canonicalizationHeader = dkimSignatureParts["c"];
                    canonicalizationBody = dkimSignatureParts["c"];
                }
            }


            // Verify body hash 'bh' using algorithm defined in 'a'
            if (!dkimSignatureParts.ContainsKey("bh"))
                throw new Exception("DKIM-Signature body hash is missing");
            if (!dkimSignatureParts.ContainsKey("a"))
                throw new Exception("DKIM-Signature algoritm is missing");
            if (dkimSignatureParts["a"] != "rsa-sha256")
                throw new Exception("DKIM-Signature algoritm is unsupported" + dkimSignatureParts["a"]);

            file.Position = 0;

            var hash = SHA256.Create();
            if (hash == null)
                throw new Exception("Could not initialize SHA-256 hasher");

            CanonicalizatedBodyStream.ForwardStreamPositionToBody(file);

            string hashout = Convert.ToBase64String(hash.ComputeHash(new CanonicalizatedBodyStream(file, canonicalizationBody == "simple")));            

            if (hashout != dkimSignatureParts["bh"])
                throw new Exception("DKIM Body hash is incorrect");

            if (!dkimSignatureParts.ContainsKey("h"))
                throw new Exception("DKIM-Signature is missing header fields");
            var headerFields = dkimSignatureParts["h"].Split(':').Select(a=>a.Trim()).ToList();
            headerFields.Add("DKIM-Signature"); // Not mentioned but always added

            // Create canonicalizated header data
            var headerData = "";
            var shouldCanonicalizeHeaders = (canonicalizationHeader == "relaxed");
            Dictionary<string, int> headerOffsetCount = new Dictionary<string, int>();
            foreach (var headerField in headerFields)
            {
                var headerLines = SmtpUtil.GetHeaderValues(file, headerField, includeFullLine: true, canonicalizated: shouldCanonicalizeHeaders, onlyIncludeFirst: (headerField == "DKIM-Signature")); // TODO: There can be multiple DKIM-Signature records in 1 email, we have to test them all seperately

                // Make sure to always pick the last one we haven't had. Ignore if we don't have more of the headers
                if (!headerOffsetCount.ContainsKey(headerField))
                    headerOffsetCount.Add(headerField, 0);
                if (headerLines.Count <= headerOffsetCount[headerField])
                    continue; // ignore missing headers
                var headerLine = headerLines.Skip(headerLines.Count - (1 + headerOffsetCount[headerField])).First();
                headerOffsetCount[headerField]++;

                if (headerData.Length > 0)
                    headerData += "\r\n";

                if (headerField == "DKIM-Signature") // Remove b= value
                {
                    var sigStartPos = headerLine.Replace("\t", " ").IndexOf(" b=");
                    if (sigStartPos < 0)
                        throw new Exception("DKIM header not supported");
                    sigStartPos += 3;
                    headerData += headerLine.Substring(0, sigStartPos);
                    var sigEndPos = headerLine.IndexOf(";", sigStartPos);
                    if (sigEndPos >= 0)
                        headerData += headerLine.Substring(sigEndPos);
                }
                else
                {
                    headerData += headerLine;
                }
            }


            // Retrieve DNS data
            if (!dkimSignatureParts.ContainsKey("d") || !dkimSignatureParts.ContainsKey("s"))
                throw new Exception("DKIM-Signature is missing domain and/or selector part");
                
            var dkimSelector = dkimSignatureParts["s"].ToLower();
            var dkimDomain = dkimSignatureParts["d"].ToLower();
            var dnsRequestDomain = dkimSelector + "._domainkey." + dkimDomain;
            var dnsResult = await SmtpUtil.GetDNSResult(dnsRequestDomain, "txt"); // [dkim s parameter from header]._domainkey.[dkim d parameter from header]

            if (dnsResult == null || !dnsResult.Any(a=>a.Contains("p=")))
                throw new Exception("DKIM record is missing from domain " + dnsRequestDomain);
                
            // v=DKIM1;t=s;k=rsa;p=MIIBIjANB...7IqC47QIDAQAB
            var dkimRecordParts = dnsResult.First(a=>a.Contains("p=")).Split(';')
                .Select(a => a.Trim().Replace("\t", "").Split('=', 2))
                .Where(a => a.Length == 2)
                .ToDictionary(a => a[0], a => a[1]);                                                        

            if (dkimRecordParts.ContainsKey("v") && dkimRecordParts["v"].ToLower() != "dkim1")
                throw new Exception("DKIM version " + dkimRecordParts["v"] + " is not supported");

            if (!dkimRecordParts.ContainsKey("p"))
                throw new Exception("DKIM Public key is missing from DNS record");

            // Verify signature
            var algorithm = "rsa";
            if (dkimRecordParts.ContainsKey("k"))
                algorithm = dkimRecordParts["k"];

            var signature = Convert.FromBase64String(dkimSignatureParts["b"].Replace(" ", "").Trim());
            var publicKey = "-----BEGIN PUBLIC KEY-----\r\n" + dkimRecordParts["p"].Replace(" ", "").Trim() + "\r\n-----END PUBLIC KEY-----";

            if (algorithm == "rsa")
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(publicKey);
                if (!rsa.VerifyData(ASCIIEncoding.ASCII.GetBytes(headerData), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new Exception("DKIM Signature is incorrect");
            }
            else if (algorithm == "ed25519 TODO not supported")
            {
                // TODO
            }
            else
            {
                throw new Exception("DKIM Algorithm " + algorithm + " is unsupported");
            }

            return dkimDomain;
        }
        public static string? GetHeaderValue(Stream file, string header)
        {
            return GetHeaderValues(file, header).FirstOrDefault();
        }
        public static List<string> GetHeaderValues(Stream file, string header, bool includeFullLine = false, bool canonicalizated = false, bool onlyIncludeFirst = true)
        {
            var values = new List<string>();
            header = header.Trim().ToLower();
            // Canonicalizated:
            // - Convert all header field names (not the header field values) to lowercase.For example, convert "SUBJect: AbC" to "subject: AbC".
            // - Unfold all header field continuation lines as described in [RFC5322] ; in particular, lines with terminators embedded in continued header field values(that is, CRLF sequences followed by WSP) MUST be interpreted without the CRLF.  Implementations MUST NOT remove the CRLF at the end of the header field value.
            // - Convert all sequences of one or more WSP characters to a single SP character. WSP characters here include those before and after a line folding boundary.
            // - Delete all WSP characters at the end of each unfolded header field value.
            // - Delete any WSP characters remaining before and after the colon separating the header field name from the header field value. The colon separator MUST be retained.

            file.Position = 0;
            var tr = new StreamReader(file);
            var line = "";
            var result = "";
            bool inHeaderData = false;
            while (!string.IsNullOrEmpty(line = tr.ReadLine()))
            {
                if (inHeaderData)
                {
                    if (line.StartsWith(" ") || line.StartsWith("\t")) // multiple lines
                    {
                        if (canonicalizated) // Remove enters, extra white spaces will be removed later 
                        {
                            // But if there was no value on the first line, we will trim the start
                            if (result.EndsWith(":") && result.IndexOf(":") == result.Length - 1)
                                line = line.TrimStart();
                            result += line; 
                        }
                        else
                            result += "\r\n" + line;
                    }
                    else
                    {
                        values.Add(result);
                        result = "";
                        if (onlyIncludeFirst)
                            break;
                        inHeaderData = false;
                    }
                }
                
                if (line.ToLower().StartsWith(header.ToLower() + ":"))
                {
                    if (result.Length > 0) // Multiple lines
                        result += "\r\n";

                    if (includeFullLine)
                    {
                        if (canonicalizated)
                            result += line.Substring(0, line.IndexOf(":")).ToLower().Trim() + ":" + line.Substring(line.IndexOf(":") + 1).Trim(); // Convert to lowercase and remove spaces between name and value
                        else
                            result += line;
                    }
                    else
                        result += line.Substring(line.IndexOf(":") + 1).Trim();
                    inHeaderData = true;
                }
            }
            if (result != "")
                values.Add(result);

            if (canonicalizated)
            {
                for (var j=0;j<values.Count; j++)
                {
                    // Remove all 2 or more whitespaces, replace tabs with spaces
                    var newResult = "";
                    var curResult = values[j];
                    for (var i = 0; i < curResult.Length; i++)
                    {
                        bool isWhiteSpace = curResult[i] == ' ' || curResult[i] == '\t';

                        if (isWhiteSpace && newResult.Length > 0 && newResult[newResult.Length - 1] == ' ')
                            continue;
                        if (isWhiteSpace)
                            newResult += " ";
                        else
                            newResult += curResult[i];
                    }
                    values[j] = newResult.Trim(); // Remove white space at the end 
                }
            }
            file.Position = 0;
            return values;
        }

        class CanonicalizatedBodyStream : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotImplementedException();

            public override long Position { get => 0; set { } }

            private StreamReader EmailBody;
            private bool Simple;
            public CanonicalizatedBodyStream(Stream emailBody, bool simple=true)
            {
                // TODO: Support using actual encoding
                EmailBody = new StreamReader(emailBody, Encoding.UTF8);
                Simple = simple;
            }
            public static void ForwardStreamPositionToBody(Stream email)
            {
                byte[] check = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
                int checkIndex = 0;
                int curByte = 0;
                while ((curByte = email.ReadByte()) >= 0)
                {
                    if (check[checkIndex] == (byte)curByte)
                    {
                        checkIndex++;
                        if (checkIndex == check.Length)
                            break; // Stop directly at the start of the body 
                    }
                    else
                    {
                        checkIndex = 0;
                    }
                }

                if (checkIndex != check.Length)
                    throw new Exception("Body missing from email");
            }


            private byte[]? CurrentOutgoingData = null;
            private int CurrentIncomingDataPos = 0;
            private bool Finished = false;
            private bool WeHadAtLeastOneLineOfData = false;
            public override int Read(byte[] buffer, int offset, int count)
            {                

                if (CurrentOutgoingData != null) // See if there is any data we still have to sent
                {
                    if (count >= CurrentOutgoingData.Length - CurrentIncomingDataPos) // We can completely sent our outgoing buffer
                    {
                        var dataCount = CurrentOutgoingData.Length - CurrentIncomingDataPos;
                        Buffer.BlockCopy(CurrentOutgoingData, CurrentIncomingDataPos, buffer, offset, dataCount);
                        CurrentOutgoingData = null;
                        CurrentIncomingDataPos = 0;
                        return dataCount;
                    }
                    else // We are limited by the count passed to this Read call
                    {
                        Buffer.BlockCopy(CurrentOutgoingData, CurrentIncomingDataPos, buffer, offset, count);
                        CurrentIncomingDataPos += count;
                        return count;
                    }
                }
                if (Finished)
                    return 0;


                var dataToSent = "";
                var data = EmailBody.ReadLine();
                while (data == "") // Empty line
                {
                    dataToSent += "\r\n";
                    data = EmailBody.ReadLine();
                    if (data == null)
                        break;
                }

                if (data == null)
                {
                    // End of file, Make sure to always end with just 1 white line
                    data = "";
                    Finished = true;
                    if (WeHadAtLeastOneLineOfData)
                        return 0;
                }
                else
                {
                    data = dataToSent + data;
                }


                // Simple: The "simple" body canonicalization algorithm ignores all empty lines at the end of the message body.
                // If there is no body or no trailing CRLF on the message body, a CRLF is added.
                if (!Simple)
                {
                    // Relaxed: As above and:
                    // Ignore all whitespace at the end of lines
                    // Reduce all sequences of WSP within a line to a single SP character.
                    var newData = "";
                    for (var i = 0; i < data.Length; i++)
                    {
                        bool isWhiteSpace = data[i] == ' ' || data[i] == '\t';

                        if (isWhiteSpace && newData.Length > 0 && newData[newData.Length - 1] == ' ')
                            continue;
                        if (isWhiteSpace)
                            newData += " ";
                        else
                            newData += data[i];
                    }
                    data = newData.TrimEnd(' '); // Remove white space at the end 
                }
                data = data + "\r\n";
                WeHadAtLeastOneLineOfData = true; 

                CurrentOutgoingData = Encoding.UTF8.GetBytes(data);
                CurrentIncomingDataPos = 0;
                return Read(buffer, offset, count);
            }            


            public override void Flush()
            { }
            public override long Seek(long offset, SeekOrigin origin)
            {
                return 0;
            }

            public override void SetLength(long value)
            {}

            public override void Write(byte[] buffer, int offset, int count)
            {}
        }

        // Thanks to https://stackoverflow.com/questions/28406888/c-sharp-rsa-public-key-output-not-correct/28407693#28407693
        public static string ExportPublicKey(RSACryptoServiceProvider csp, bool includeBeginAndEnd=false)
        {
            var sb = new StringBuilder();

            var parameters = csp.ExportParameters(false);
            if (parameters.Modulus == null || parameters.Exponent == null)
                throw new Exception("Could not export the public key parameters");
            using (var stream = new MemoryStream())
            {
                var writer = new BinaryWriter(stream);
                writer.Write((byte)0x30); // SEQUENCE
                using (var innerStream = new MemoryStream())
                {
                    var innerWriter = new BinaryWriter(innerStream);
                    innerWriter.Write((byte)0x30); // SEQUENCE
                    EncodeLength(innerWriter, 13);
                    innerWriter.Write((byte)0x06); // OBJECT IDENTIFIER
                    var rsaEncryptionOid = new byte[] { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01 };
                    EncodeLength(innerWriter, rsaEncryptionOid.Length);
                    innerWriter.Write(rsaEncryptionOid);
                    innerWriter.Write((byte)0x05); // NULL
                    EncodeLength(innerWriter, 0);
                    innerWriter.Write((byte)0x03); // BIT STRING
                    using (var bitStringStream = new MemoryStream())
                    {
                        var bitStringWriter = new BinaryWriter(bitStringStream);
                        bitStringWriter.Write((byte)0x00); // # of unused bits
                        bitStringWriter.Write((byte)0x30); // SEQUENCE
                        using (var paramsStream = new MemoryStream())
                        {
                            var paramsWriter = new BinaryWriter(paramsStream);
                            EncodeIntegerBigEndian(paramsWriter, parameters.Modulus); // Modulus
                            EncodeIntegerBigEndian(paramsWriter, parameters.Exponent); // Exponent
                            var paramsLength = (int)paramsStream.Length;
                            EncodeLength(bitStringWriter, paramsLength);
                            bitStringWriter.Write(paramsStream.GetBuffer(), 0, paramsLength);
                        }
                        var bitStringLength = (int)bitStringStream.Length;
                        EncodeLength(innerWriter, bitStringLength);
                        innerWriter.Write(bitStringStream.GetBuffer(), 0, bitStringLength);
                    }
                    var length = (int)innerStream.Length;
                    EncodeLength(writer, length);
                    writer.Write(innerStream.GetBuffer(), 0, length);
                }

                var base64 = ASCIIEncoding.ASCII.GetBytes(Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length));
                if (includeBeginAndEnd)
                    sb.AppendLine("-----BEGIN PUBLIC KEY-----");
                for (var i = 0; i < base64.Length; i += 64)
                {                    
                    sb.Append(ASCIIEncoding.ASCII.GetString(base64, i, Math.Min(64, base64.Length - i)));
                    if (includeBeginAndEnd)
                        sb.AppendLine();
                }
                if (includeBeginAndEnd)
                    sb.AppendLine("-----END PUBLIC KEY-----");
            }

            return sb.ToString();
        }

        private static void EncodeLength(BinaryWriter stream, int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException("length", "Length must be non-negative");
            if (length < 0x80)
            {
                // Short form
                stream.Write((byte)length);
            }
            else
            {
                // Long form
                var temp = length;
                var bytesRequired = 0;
                while (temp > 0)
                {
                    temp >>= 8;
                    bytesRequired++;
                }
                stream.Write((byte)(bytesRequired | 0x80));
                for (var i = bytesRequired - 1; i >= 0; i--)
                {
                    stream.Write((byte)(length >> (8 * i) & 0xff));
                }
            }
        }

        private static void EncodeIntegerBigEndian(BinaryWriter stream, byte[] value, bool forceUnsigned = true)
        {
            stream.Write((byte)0x02); // INTEGER
            var prefixZeros = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != 0) break;
                prefixZeros++;
            }
            if (value.Length - prefixZeros == 0)
            {
                EncodeLength(stream, 1);
                stream.Write((byte)0);
            }
            else
            {
                if (forceUnsigned && value[prefixZeros] > 0x7f)
                {
                    // Add a prefix zero to force unsigned if the MSB is 1
                    EncodeLength(stream, value.Length - prefixZeros + 1);
                    stream.Write((byte)0);
                }
                else
                {
                    EncodeLength(stream, value.Length - prefixZeros);
                }
                for (var i = prefixZeros; i < value.Length; i++)
                {
                    stream.Write(value[i]);
                }
            }
        }


    }
}
