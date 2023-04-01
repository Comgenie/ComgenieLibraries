using Comgenie.Server.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers
{
    public class SmtpHandler : IConnectionHandler
    {
        private Dictionary<string, string> EmailForwards = new Dictionary<string, string>();
        public bool EnableSPFCheck = true;
        public bool EnableDKIMCheck = true;
        public bool EnableDMARCCheck = true;
        public bool EnableStartTLS = true;

        public SmtpHandler()
        {
        }       

        public void ClientConnect(Client client)
        {
            Log.Debug(nameof(SmtpHandler), "SMTP client connected");
            client.Data = new SmtpClientData()
            {
                IncomingBuffer = new byte[1024 * 514],  // A small bit larger than the buffer in the Server class, as we sometimes keep a little bit of data in the buffer
                RcptTo = new List<string>(),
                MailBox = new List<string>()
            };

            try
            {
                client.SendString("220 " + client.Server.DefaultDomain + " SMTP\r\n");
            }
            catch { } // Just in case the client already disconnected again, TODO: Make sure this is done on a Worker thread and not the accept-connection thread
        }

        public void ClientDisconnect(Client client)
        {
            Log.Debug(nameof(SmtpHandler), "SMTP client disconnected");
            var data = (SmtpClientData)client.Data;
            if (data != null && data.FileDataStream != null)
            {
                ProcessIncomingEmail(client);                
            }
        }
        private Func<string, bool> checkMailboxCallback = null;
        private Func<string, string, bool> checkAuthCallback = null;
        private Action<SmtpClientData> incomingEmailCallback = null;
        public void SetIncomingEmailCallback(Action<SmtpClientData> callBack)
        {
            incomingEmailCallback = callBack;
        }
        public void SetInboxCheckCallback(Func<string, bool> callBack)
        {
            checkMailboxCallback = callBack;
        }
        public void SetAuthenticationCheckCallback(Func<string, string, bool> callBack) 
        {
            checkAuthCallback = callBack;
        }


        public void AddEmailForward(string emailFilter, string forwardAddress)
        {
            EmailForwards[emailFilter.ToLower()] = forwardAddress;
        }
        public void RemoveEmailForward(string emailFilter)
        {
            if (EmailForwards.ContainsKey(emailFilter.ToLower()))
                EmailForwards.Remove(emailFilter.ToLower());
        }

        public void ProcessIncomingEmail(Client client)
        {
            var forwardFailedDirectory = "forward-failed";
            var data = (SmtpClientData)client.Data;

            data.FileDataStream.Close();
            data.FileDataStream = null;
            foreach (var mailbox in data.MailBox)
            {
                string toAddress = null;

                if (EmailForwards.ContainsKey(mailbox))
                    toAddress = EmailForwards[mailbox];
                else if (mailbox.Contains("@") && EmailForwards.ContainsKey(mailbox.Substring(mailbox.IndexOf("@") + 1)))
                    toAddress = EmailForwards[mailbox.Substring(mailbox.IndexOf("@") + 1)];

                if (toAddress != null) {
                    try
                    {
                        using (var stream = File.OpenRead(data.FileName))
                            SmtpUtil.SendEmailRaw(SmtpUtil.GetAddressDomain(toAddress), mailbox, new string[] { toAddress }, stream, true);
                    }
                    catch (Exception e)
                    {
                        if (!Directory.Exists(forwardFailedDirectory))
                            Directory.CreateDirectory(forwardFailedDirectory);
                        File.Copy(data.FileName, Path.Combine(forwardFailedDirectory, Path.GetFileName(data.FileName)));
                        File.WriteAllText(Path.Combine(forwardFailedDirectory, Path.GetFileName(data.FileName)) + ".txt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " (UTC):\r\nTO: "+ toAddress+"\r\n" + e.GetType().Name + " - " + e.Message + "\r\n" + e.StackTrace);
                    }
                }
            }


            if (EnableDKIMCheck)
            {
                try
                {
                    // See if mail contains a valid dkim record
                    using (var readStream = File.OpenRead(data.FileName))
                    {
                        var dkimDomain = SmtpUtil.CheckDKIM(readStream);
                        if (dkimDomain != null)
                        {
                            data.DKIM_Domain = dkimDomain;
                            data.DKIM_Pass = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(nameof(SmtpHandler), "Could not verify DKIM (" + e.Message + ") for email from " + data.MailFrom + "\r\n" + e.StackTrace);
                    data.DKIM_FailReason = e.Message;
                }
            }

            if (EnableSPFCheck)
            {
                try
                {
                    // See if this IP address is approved in the SPF records
                    data.SPF_Pass = SmtpUtil.CheckSPF(data.SPF_IP);
                }
                catch (Exception e)
                {
                    Log.Warning(nameof(SmtpHandler), "Could not verify SPF (" + e.Message + ") for email from " + data.MailFrom + "\r\n" + e.StackTrace);
                }
            }

            if (EnableDMARCCheck)
            {
                // DMARC just suggests an action we have to do after verifying dkim/spf 
                // TODO
            }

            if (incomingEmailCallback != null)
                incomingEmailCallback(data);
        }

        public void ClientReceiveData(Client client, byte[] buffer, int len)
        {
            var data = (SmtpClientData)client.Data;
            if (data.IncomingBufferLength + len > data.IncomingBuffer.Length)
            {
                Log.Warning(nameof(SmtpHandler), "SMTP buffer too small");
                return; // Buffer too small
            }

            Buffer.BlockCopy(buffer, 0, data.IncomingBuffer, data.IncomingBufferLength, len);
            data.IncomingBufferLength += len;

            while (data.IncomingBufferLength > 0)
            {
                // Check for line breaks, or double line breaks
                bool handledCommand = false;
                for (var i=0;i<data.IncomingBufferLength;i++)
                {
                    if (!data.InDataPart && i + 1 < data.IncomingBufferLength && data.IncomingBuffer[i] == '\r' &&  data.IncomingBuffer[i + 1] == '\n') 
                    {
                        // Normal commands
                        string line = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, 0, i);
                        ClientHandleCommand(client, line);
                        Buffer.BlockCopy(data.IncomingBuffer, i + 2, data.IncomingBuffer, 0, data.IncomingBufferLength - (i + 2)); // Move the rest to the front of the buffer
                        data.IncomingBufferLength -= (i + 2);
                        handledCommand = true;

                        break;
                    } else if (data.InDataPart && i + 4 < data.IncomingBufferLength && data.IncomingBuffer[i] == '\r' && data.IncomingBuffer[i + 1] == '\n' && data.IncomingBuffer[i + 2] == '.' && data.IncomingBuffer[i + 3] == '\r' && data.IncomingBuffer[i + 4] == '\n') {
                        // End of data
                        data.FileDataStream.Write(data.IncomingBuffer, 0, i + 2); // the \r\n is part of the email data
                        ProcessIncomingEmail(client);

                        client.SendString("250 Ok\r\n");                                                

                        Buffer.BlockCopy(data.IncomingBuffer, i + 5, data.IncomingBuffer, 0, data.IncomingBufferLength - (i + 5)); // Move the rest to the front of the buffer
                        data.IncomingBufferLength -= (i + 5);
                        data.InDataPart = false;
                        handledCommand = true;
                        break;
                    }
                    else if (data.InDataPart && i + 4 < data.IncomingBufferLength && data.IncomingBuffer[i] == '\r' && data.IncomingBuffer[i + 1] == '\n' && data.IncomingBuffer[i + 2] == '.') // the + 4 is correct, this check should only be done if the above one also can be checked
                    {
                        // Dot stuffing is a thing.. if a line starts with a . followed by anything else than a line break, we should ignore that specific dot (RFC 5321, section 4.5.2)
                        data.FileDataStream.Write(data.IncomingBuffer, 0, i + 2); // the \r\n is part of the email data
                        Buffer.BlockCopy(data.IncomingBuffer, i + 3, data.IncomingBuffer, 0, data.IncomingBufferLength - (i + 3)); // Move the rest to the front of the buffer
                        data.IncomingBufferLength -= (i + 3);
                        handledCommand = true;
                        break;
                    }
                    else if (data.InDataPart && data.IncomingBufferLength > 5 && i + 1 == data.IncomingBufferLength)
                    {
                        // In the middle of data and at the end of our buffer, we will make sure the \r\n.\r\n check can still proceed so we'll leave 5 bytes in the buffer
                        data.FileDataStream.Write(data.IncomingBuffer, 0, data.IncomingBufferLength - 5);
                        Buffer.BlockCopy(data.IncomingBuffer, data.IncomingBufferLength - 5, data.IncomingBuffer, 0, 5);
                        data.IncomingBufferLength = 5;
                        handledCommand = true;
                        break;
                    }
                }

                if (!handledCommand)
                    break;
            }
            //client.SendData(buffer, 0, len); // Echo
        }

        public void ClientHandleCommand(Client client, string line)
        {
            try
            {
                Console.WriteLine(client.RemoteAddress + ": " + line);
                var data = (SmtpClientData)client.Data;
                data.SPF_IP = client.RemoteAddress;

                var parts = line.Split(' ', 2);
                parts[0] = parts[0].ToUpper();

                if (data.SmtpAuthMethod != null)
                {
                    if (data.SmtpAuthMethod == "LOGIN")
                    {
                        if (data.SmtpAuthUsername == null)
                        {
                            data.SmtpAuthUsername = ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(line));
                            client.SendString("334 UGFzc3dvcmQ6\r\n"); // Base 64 encoded 'Password'
                        }
                        else
                        {
                            data.SmtpAuthPassword = ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(line));
                                                        
                            if (checkAuthCallback != null && checkAuthCallback(data.SmtpAuthUsername, data.SmtpAuthPassword))
                            {
                                data.IsAuthenticated = true;
                                client.SendString("235 2.7.0 Authentication successful\r\n");    
                            }
                            else
                            {
                                client.SendString("535 5.7.8 Authentication credentials invalid\r\n");
                            }
                            data.SmtpAuthMethod = null;
                            
                        }                        
                    }
                    else if (data.SmtpAuthMethod == "PLAIN")
                    {
                        line = ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(line));
                        parts = line.Split('\0');
                        if (parts.Length == 3 && checkAuthCallback != null && checkAuthCallback(parts[0], parts[1]))
                        {
                            data.SmtpAuthUsername = parts[0];
                            data.SmtpAuthPassword = parts[1];
                            client.SendString("235 2.7.0 Authentication successful\r\n");
                            data.IsAuthenticated = true;
                        }
                        else
                        {
                            client.SendString("535 5.7.8 Authentication credentials invalid\r\n");
                        }
                        data.SmtpAuthMethod = null;
                    }

                    return;
                }

                if (parts[0] == "HELO" && parts.Length > 1)
                {
                    data.HeloInfo = parts[1];
                    client.SendString("250 Helo... Is it me you're looking for? " + parts[1] + "\r\n");
                }
                else if (parts[0] == "EHLO" && parts.Length > 1) // Also send some more info
                {
                    data.HeloInfo = parts[1] + " [" + client.RemoteAddress + "]";
                    Console.WriteLine("Sending EHLO response");
                    var extensionExtras = "";
                    if (checkAuthCallback != null)
                        extensionExtras += "250-AUTH LOGIN PLAIN\r\n";
                    if (EnableStartTLS)
                        extensionExtras += "250-STARTTLS\r\n";
                    client.SendString("250-" + client.Server.DefaultDomain + " Ehlo... Is it me you're looking for? " + data.HeloInfo + "\r\n250-SIZE 157286400\r\n250-PIPELINING\r\n"+extensionExtras +"250 8BITMIME\r\n");
                }
                else if (parts[0] == "MAIL" && parts.Length > 0) // Mail from
                {
                    var pos = line.IndexOf(":");
                    if (pos < 0)
                    {
                        client.SendString("500 Error\r\n");
                        return;
                    }
                    data.MailFrom = line.Substring(pos + 1).Trim();
                    client.SendString("250 OK\r\n");
                }
                else if (parts[0] == "RCPT" && parts.Length > 1) // Rcpt to
                {
                    if (data.MailBox.Count > 10)
                    {
                        client.SendString("452 Too many recipients\r\n");
                        return;
                    }

                    var pos = line.IndexOf(":");
                    if (pos < 0)
                    {
                        client.SendString("500 Error\r\n");
                        return;
                    }
                    var rcptTo = line.Substring(pos + 1);

                    var mailBox = SmtpUtil.GetMailAddress(rcptTo);                    

                    var approved = false;
                    if (mailBox != null)
                    {
                        if (checkMailboxCallback != null) // Custom handler 
                            approved = checkMailboxCallback(mailBox);
                        else if (data.IsAuthenticated)
                            approved = true; // Allow relay when authenticated
                        else // By default accept all registered domains 
                            approved = client.Server.Domains.Contains(mailBox.Substring(mailBox.LastIndexOf("@") + 1).ToLower());
                    }

                    if (approved)
                    {
                        if (!data.RcptTo.Contains(rcptTo))
                            data.RcptTo.Add(rcptTo);
                        if (!data.MailBox.Contains(mailBox))
                            data.MailBox.Add(mailBox);
                        client.SendString("250 OK\r\n");
                    }
                    else
                    {
                        client.SendString("550 relay not permitted\r\n");
                    }
                }
                else if (parts[0] == "DATA") // Also send some more info
                {
                    if (data.MailBox.Count == 0)
                    {
                        client.SendString("550 No recipients defined\r\n");
                        return;
                    }

                    data.InDataPart = true;
                    data.FileName = "mail-" + Guid.NewGuid().ToString() + ".eml";
                    data.FileDataStream = File.OpenWrite(data.FileName);
                    client.SendString("354 End data with <CR><LF>.<CR><LF>\r\n");
                }
                else if (parts[0] == "AUTH" && parts.Length > 1) 
                {
                    data.SmtpAuthUsername = null;
                    data.SmtpAuthPassword = null;
                    data.SmtpAuthMethod = null;
                    data.IsAuthenticated = false;

                    if (checkAuthCallback == null)
                    {
                        // Not supported and used yet, so any attempt is a h4x0r
                        Log.Warning(nameof(SmtpHandler), "Added " + client.RemoteAddress + " to ban list");
                        Server.IPBanList.Add(client.RemoteAddress);
                        Server.SaveBanList();

                        client.SendString("535 Not supported\r\n");
                        client.Handler.ClientDisconnect(client);
                        client.Disconnect();
                    }
                    else if (parts[1].ToUpper() == "PLAIN")
                    {
                        client.SendString("334 \r\n"); // Go ahead with the plain (but base64 encoded) credentials
                        data.SmtpAuthMethod = "PLAIN";
                    }
                    else if (parts[1].ToUpper().StartsWith("PLAIN ")) // One line auth command
                    {
                        line = ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(parts[1].Substring(6)));
                        parts = line.Split('\0');
                        if (parts.Length == 3 && checkAuthCallback != null && checkAuthCallback(parts[0], parts[1]))
                        {
                            data.SmtpAuthUsername = parts[0];
                            data.SmtpAuthPassword = parts[1];
                            client.SendString("235 2.7.0 Authentication successful\r\n");
                            data.IsAuthenticated = true;
                        }
                        else
                        {
                            client.SendString("535 5.7.8 Authentication credentials invalid\r\n");
                        }                        

                    }
                    else if (parts[1].ToUpper() == "LOGIN")
                    {
                        client.SendString("334 VXNlcm5hbWU6\r\n"); // Base 64 encoded 'Username'
                        data.SmtpAuthMethod = "LOGIN";
                    }
                    else if (parts[1].ToUpper().StartsWith("LOGIN "))
                    {
                        data.SmtpAuthUsername = ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(parts[1].Substring(6)));
                        data.SmtpAuthMethod = "LOGIN";
                        client.SendString("334 UGFzc3dvcmQ6\r\n"); // Base 64 encoded 'Password'                        
                    }
                    /*else if (parts[1] == "CRAM-MD5") // Go ahead with the md5 (and base64 encoded) credentials with challenge
                    {
                        client.SendString("334 \r\n");
                        data.SmtpAuthMethod = parts[1];
                    }*/
                }
                else if (parts[0] == "QUIT")
                {
                    client.SendString("221 Bye\r\n");
                    client.Handler.ClientDisconnect(client);
                    client.Disconnect();

                }
                else if (parts[0] == "STARTTLS" && EnableStartTLS)
                {
                    client.Server.EnableSSLOnClient(client, null, () =>
                    {
                        // Send this message in this callback to make sure no SSL packets is accidentally read before initializing sslstream
                        client.SendString("220 go ahead\r\n");
                    });
                }
                else if (parts[0] == "NOOP")
                {
                    client.SendString("250 Ok\r\n");
                }
                else if (parts[0] == "RSET") // Reset current mail from/rcpt to/mailbox info
                {
                    data.RcptTo.Clear();
                    data.MailBox.Clear();
                    data.MailFrom = null;
                    data.DKIM_Domain = null;
                    data.DKIM_Pass = false;
                    data.SPF_Pass = false; // Don't reset IP address
                    data.DMARC_Action = null;

                    client.SendString("250 Ok\r\n");
                }
                else
                {
                    client.SendString("502 Command not implemented\r\n");
                }
            }
            catch (Exception e)
            {
                Log.Warning(nameof(SmtpHandler), "Could not handle SMTP command: " + e.Message);
            }
        }
    }

    public class SmtpClientData
    {
        public string HeloInfo { get; set; }
        public string MailFrom { get; set; }
        public List<string> RcptTo { get; set; }
        public List<string> MailBox { get; set; }
        public byte[] IncomingBuffer { get; set; }
        public int IncomingBufferLength { get; set; }
        public bool InDataPart { get; set; }
        public Stream FileDataStream { get; set; }
        public string FileName { get; set; }

        public string SmtpAuthMethod { get; set; }
        public string SmtpAuthUsername { get; set; }
        public string SmtpAuthPassword { get; set; }
        public bool IsAuthenticated { get; set; }

        // Check result
        public string DKIM_Domain { get; set; }
        public bool DKIM_Pass { get; set; }
        public string DKIM_FailReason { get; set; }

        public string SPF_IP { get; set; }
        public bool? SPF_Pass { get; set; }

        public string DMARC_Action { get; set; }

    }
}
