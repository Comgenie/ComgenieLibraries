using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers
{
    public class ImapHandler : IConnectionHandler
    {
        public void ClientConnect(Client client)
        {
            client.Data = new ImapData()
            {
                IncomingBuffer = new byte[1024 * 514],  
            };

            try
            {
                client.SendString("* OK " + client.Server.DefaultDomain + " Service Ready\r\n");
            }
            catch { } // Just in case the client already disconnected again, TODO: Make sure this is done on a Worker thread and not the accept-connection thread
        }

        public void ClientDisconnect(Client client)
        {
            Log.Debug(nameof(ImapHandler), "IMAP client disconnected");
            var data = (ImapData)client.Data;
            
        }

        public void ClientReceiveData(Client client, byte[] buffer, int len)
        {
            var data = (ImapData)client.Data;
            if (data.IncomingBufferLength + len > data.IncomingBuffer.Length)
            {
                Log.Info(nameof(ImapHandler), "IMAP buffer too small");
                return; // Buffer too small
            }

            Buffer.BlockCopy(buffer, 0, data.IncomingBuffer, data.IncomingBufferLength, len);
            data.IncomingBufferLength += len;

            while (data.IncomingBufferLength > 0)
            {
                // Check for line breaks
                bool handledCommand = false;
                for (var i = 0; i < data.IncomingBufferLength; i++)
                {
                    if (i + 1 < data.IncomingBufferLength && data.IncomingBuffer[i] == '\r' && data.IncomingBuffer[i + 1] == '\n')
                    {
                        // Normal commands
                        string line = ASCIIEncoding.ASCII.GetString(data.IncomingBuffer, 0, i);
                        ClientHandleCommand(client, line);
                        Buffer.BlockCopy(data.IncomingBuffer, i + 2, data.IncomingBuffer, 0, data.IncomingBufferLength - (i + 2)); // Move the rest to the front of the buffer
                        data.IncomingBufferLength -= (i + 2);
                        handledCommand = true;
                        break;
                    }
                }

                if (!handledCommand)
                    break;
            }
        }
        public void ClientHandleCommand(Client client, string line)
        {
            var data = (ImapData)client.Data;
            var parts = line.Split(' ', 3);
            if (parts.Length < 2)
                return;
            parts[1] = parts[1].ToUpper();
            try
            {
                if (parts[1] == "LOGIN") // 
                {


                }
                else if (parts[1] == "CAPABILITY")
                {
                    client.SendString(parts[0] + " IMAP4rev1 UNSELECT IDLE NAMESPACE QUOTA ID XLIST CHILDREN X-GM-EXT-1 XYZZY SASL-IR AUTH=XOAUTH AUTH=XOAUTH2 AUTH=PLAIN AUTH=PLAIN-CLIENTTOKEN\r\n");
                }
                else if (parts[1] == "SELECT") // select [mailboxname]
                {

                }
                else if (parts[1] == "LIST") // LIST [path] [wildcard]   List all mailboxes/folders
                {
/*A1 list "INBOX/" "*"
* LIST (HasNoChildren) "/" INBOX/some_other_folder
* LIST (HasNoChildren UnMarked Archive) "/" INBOX/Archive
* LIST (HasNoChildren UnMarked Sent) "/" INBOX/Sent
* LIST (HasNoChildren Marked Trash) "/" INBOX/Trash
* LIST (HasNoChildren Marked Junk) "/" INBOX/Spam
* LIST (HasNoChildren UnMarked Drafts) "/" INBOX/Drafts
A1 OK List completed (0.000 + 0.000 secs).*/


                }
                else if (parts[1] == "FETCH") // select [item] [
                {

                }
                else if (parts[1] == "LOGOUT")
                {

                }
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(ImapHandler), "Error handling imap command: " + ex.Message);

            }
        }

        public class ImapData
        {
            public byte[] IncomingBuffer { get; set; }
            public int IncomingBufferLength { get; set; }
        }
    }
}
