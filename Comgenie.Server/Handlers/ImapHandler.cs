using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers
{
    public class ImapHandler : IConnectionHandler
    {        

        private Func<ImapClientData, string, string, bool>? AuthenticationCallBack { get; set; }
        private Action<ImapClientData, MailboxAction, string, string?>? MailboxActionCallBack { get; set; }
        private Func<ImapClientData, List<string>>? ListMailboxesCallBack { get; set; } 
        private Func<ImapClientData, string, IQueryable<ImapItem>>? ImapItemsHandler { get; set; }
        private Func<ImapClientData, long, Stream>? ImapGetContentHandler { get; set; }
        public enum MailboxAction
        {
            Create,
            Delete,
            Rename
        }

        /// <summary>
        /// Set a function to handle the authentication check. 
        /// An username and password will be provided and the function should return true if the authentication details are correct
        /// </summary>
        /// <param name="authenticationCallBack">Function expecting (ImapClientData clientData, string username, string password) and returning bool isValid</param>
        public void SetAuthenticationCheckCallback(Func<ImapClientData, string, string, bool> authenticationCallBack)
        {
            AuthenticationCallBack = authenticationCallBack;
        }

        /// <summary>
        /// Set an action to handle mailbox actions (create, delete, rename).
        /// </summary>
        /// <param name="mailboxAction">Action accepting (ImapClientData clientData, MailboxAction, string mailboxName, string newMailboxName). The newMailboxName argument will only be set when renaming.</param>
        public void SetMailboxActionCallBack(Action<ImapClientData, MailboxAction, string, string?> mailboxAction)
        {
            MailboxActionCallBack = mailboxAction;
        }

        /// <summary>
        /// Register a function to handle retrieving items in a mailbox. 
        /// The return value must be a queryable container of ImapItems and can be optimized for the specific sort and search calls which the client makes.
        /// </summary>
        /// <param name="listItemsCallBack">Function accepting (ImapClientData clientData, string mailboxName) and returning an IQueryable list of imap items</param>
        public void SetListItemsCallBack(Func<ImapClientData, string, IQueryable<ImapItem>> listItemsCallBack)
        {
            ImapItemsHandler = listItemsCallBack;
        }

        /// <summary>
        /// Register a function to retrieve all available mailboxes for the current user
        /// </summary>
        /// <param name="listItemsCallBack">Function accepting (ImapClientData clientData) and returning a list of the mailboxes available to the logged in user</param>
        public void SetListMailboxesCallBack(Func<ImapClientData, List<string>> listMailboxesCallBack)
        {
            ListMailboxesCallBack = listMailboxesCallBack;
        }
        public void SetImapGetContentCallBack(Func<ImapClientData, long, Stream> imapGetContentCallBack)
        {
            ImapGetContentHandler = imapGetContentCallBack;
        }

        public async Task ClientConnect(Client client)
        {
            client.Data = new ImapClientData()
            {
                Client = client,
                IncomingBuffer = new byte[1024 * 514],  
            };

            try
            {
                await client.SendString("* OK " + (client.Server?.DefaultDomain ?? "localhost") + " Service Ready\r\n");
            }
            catch { } // Just in case the client already disconnected again
        }

        public Task ClientDisconnect(Client client)
        {
            Log.Debug(nameof(ImapHandler), "IMAP client disconnected");
            //var data = (ImapClientData)client.Data;
            return Task.CompletedTask;
        }

        public async Task ClientReceiveData(Client client, byte[] buffer, int len)
        {
            var data = (ImapClientData?)client.Data;
            if (data == null)
                return;

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
                        await ClientHandleCommand(client, line);
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
        private List<string> SplitLineIntoParts(string line)
        {
            List<string> parts = new List<string>();
            var parsed = 0;
            while (parsed < line.Length)
            {
                if (line[parsed] == '(' || line[parsed] == '"')
                {
                    var level = 0;
                    var endOfGroup = parsed + 1;
                    while (endOfGroup < line.Length)
                    {
                        if (line[endOfGroup] == '(' && line[parsed] == '(')
                            level++;
                        if (line[endOfGroup] == ')' && line[parsed] == '(')
                        {
                            if (level == 0)
                                break; // Found the end
                            level--;                            
                        }
                        if (line[endOfGroup] == '"' && line[parsed] == '"')
                            break; // found the end
                        endOfGroup++;
                    }

                    if (endOfGroup == line.Length)
                    {
                        // Incorrect syntax, add the rest to one group as failsafe
                        parts.Add(line.Substring(parsed));
                        break;
                    }
                    
                    // Add the whole group as one part, excluding the ( ) 
                    parsed++;
                    parts.Add(line.Substring(parsed, endOfGroup - parsed));
                    parsed = endOfGroup + 2; // ) should be followed by a space if there is another part
                    continue;
                }
                var nextSpace = line.IndexOf(' ', parsed);
                if (nextSpace < 0)
                {
                    // No more spaces
                    parts.Add(line.Substring(parsed));
                    break;
                }

                // Also check for brackets any commands[using brackets], we will ignore the spaces in those
                var ignoreNextCount = 1;
                for (var i=parsed;i<nextSpace;i++)
                {
                    if (line[i] == '[')
                    {
                        for (var j = i + 1; j < line.Length; j++)
                        {
                            if (line[j] == ']')
                            {
                                nextSpace = j + 1;
                                ignoreNextCount = 0;
                                break;
                            }
                        }
                        break;
                    }
                }
                parts.Add(line.Substring(parsed, nextSpace - parsed));
                parsed = nextSpace + ignoreNextCount;
            }

            return parts;
        }
        public async Task ClientHandleCommand(Client client, string line)
        {
            var data = (ImapClientData?)client.Data;
            if (data == null)
                return;

            List<string> parts = SplitLineIntoParts(line);
            if (parts.Count < 2)
                return;
            Console.WriteLine("IMAP: " + line);
            var tag = parts[0];

            parts[1] = parts[1].ToUpper();

            if (parts[1] == "UID" && parts.Count >= 3) // Our UID and Sequence numbers as the same, so we can just ignore this
            {
                parts.RemoveAt(1);
                parts[1] = parts[1].ToUpper();
            }

            try
            {
                if (parts[1] == "LOGIN" && parts.Count >= 4 && client.StreamIsEncrypted) // username, password
                {
                    if (AuthenticationCallBack != null && AuthenticationCallBack(data, parts[2], parts[3]))
                    {
                        Log.Info(nameof(ImapHandler), "IMAP: Login successful as " + parts[2]);
                        data.AuthenticatedUser = parts[2];
                        await client.SendString(tag + " OK LOGIN completed\r\n");
                    }
                    else
                    {
                        Console.WriteLine("IMAP: Login failed");
                        await client.SendString(tag + " NO LOGIN failed\r\n");
                    }
                }
                else if (parts[1] == "STARTTLS" && client.Server != null) // select [item] [
                {
                    client.Server.EnableSSLOnClient(client, null, () =>
                    {
                        // Send this message in this callback to make sure no SSL packets is accidentally read before initializing sslstream
                        client.SendString(tag + " OK Begin TLS negotiation now\r\n").Wait();
                    });
                }
                else if (parts[1] == "AUTHENTICATE") // Set auth method
                {
                    /*if (parts.Length > 2 && parts[2].ToUpper() == "PLAIN")
                    {
                        client.SendString(tag + " OK sure\r\n"); 
                    }
                    else
                    {*/
                    await client.SendString(tag + " NO\r\n");
                    //}
                }
                else if (parts[1] == "CAPABILITY")
                {
                    if (client.StreamIsEncrypted)
                        await client.SendString("* CAPABILITY IMAP4rev1 AUTH=PLAIN\r\n");
                    else
                        await client.SendString("* CAPABILITY IMAP4rev1 STARTTLS LOGINDISABLED\r\n");
                    await client.SendString(tag + " OK CAPABILITY completed\r\n"); // IMAP4rev1 STARTTLS UNSELECT IDLE NAMESPACE QUOTA ID XLIST CHILDREN X-GM-EXT-1 XYZZY SASL-IR AUTH=XOAUTH AUTH=XOAUTH2 AUTH=PLAIN AUTH=PLAIN-CLIENTTOKEN
                }
                else if ((parts[1] == "SELECT" || parts[1] == "EXAMINE") && data.AuthenticatedUser != null && parts.Count > 2) // Select a mailbox and list overview information
                {
                    var mailbox = parts[2];
                    var items = new List<ImapItem>().AsQueryable();
                    if (ImapItemsHandler != null)
                        items = ImapItemsHandler(data, mailbox);

                    await client.SendString("* FLAGS (\\Answered \\Deleted \\Seen \\Draft)\r\n");  // Flags that can be used in STORE, COPY, and FETCH commands
                    await client.SendString("* " + items.Count() + " EXISTS\r\n");  // Total number of messages
                    await client.SendString("* " + items.Where(a => a.Moment > DateTime.UtcNow.AddDays(-3)).Count() + " RECENT\r\n");  // Messages with recent flag set
                    if (items.Count() > 0)
                    {
                        var first = items.OrderByDescending(a => a.Moment).FirstOrDefault();
                        var firstUnseen = items.OrderByDescending(a => a.Moment).FirstOrDefault(a => !a.Seen);
                        if (firstUnseen != null)
                            await client.SendString("* OK [UNSEEN " + firstUnseen.UID + "]\r\n");  // Id of the first unseen message
                        if (first != null)
                        {
                            await client.SendString("* OK [UIDVALIDITY " + first.UID + "]\r\n");  // Unique identifier validity value, optional
                            await client.SendString("* OK [UIDNEXT " + first.UID + "]\r\n");  // Predicted next unique identifier value, optional
                        }
                    }
                    //client.SendString("* OK [PERMANENTFLAGS (\\Deleted \\Seen \\*)]\r\n");  // Flags that can be changed permanently, optional (by default all flags can be changed permanently)

                    data.SelectedMailbox = mailbox;

                    if (parts[1] == "SELECT")
                    {
                        await client.SendString(tag + " OK [READ-WRITE] SELECT completed\r\n");
                    }
                    else // Same as select, but read only
                    {
                        await client.SendString(tag + " OK [READ-ONLY] EXAMINE completed\r\n");
                    }
                }
                else if (parts[1] == "CREATE" && data.AuthenticatedUser != null && parts.Count > 2) // create [mailboxname]
                {
                    if (MailboxActionCallBack != null)
                        MailboxActionCallBack(data, MailboxAction.Create, parts[2], null);
                    await client.SendString(tag + " OK CREATE completed\r\n");
                }
                else if (parts[1] == "DELETE" && data.AuthenticatedUser != null && parts.Count > 2) // delete [mailboxname]
                {
                    if (MailboxActionCallBack != null)
                        MailboxActionCallBack(data, MailboxAction.Delete, parts[2], null);
                    await client.SendString(tag + " OK DELETE completed\r\n");
                }
                else if (parts[1] == "RENAME" && data.AuthenticatedUser != null && parts.Count > 3) // rename [mailboxname] [newmailboxname]
                {
                    if (MailboxActionCallBack != null)
                        MailboxActionCallBack(data, MailboxAction.Rename, parts[2], parts[3]);
                    await client.SendString(tag + " OK RENAME completed\r\n");
                }
                else if (parts[1] == "LIST" && data.AuthenticatedUser != null) // LIST [path] [wildcard]   List all mailboxes/folders
                {
                    if (ListMailboxesCallBack != null)
                    {
                        var mailBoxes = ListMailboxesCallBack(data);
                        foreach (var mailbox in mailBoxes)
                        {
                            await client.SendString("* LIST (HasNoChildren) \"/\" " + mailbox + "\r\n");
                        }
                    }
                    await client.SendString(tag + " OK LIST completed\r\n");
                    /*A1 list "INBOX/" "*"
                    * LIST (HasNoChildren) "/" INBOX/some_other_folder
                    * LIST (HasNoChildren UnMarked Archive) "/" INBOX/Archive
                    * LIST (HasNoChildren UnMarked Sent) "/" INBOX/Sent
                    * LIST (HasNoChildren Marked Trash) "/" INBOX/Trash
                    * LIST (HasNoChildren Marked Junk) "/" INBOX/Spam
                    * LIST (HasNoChildren UnMarked Drafts) "/" INBOX/Drafts
                    A1 OK List completed (0.000 + 0.000 secs).*/

                }
                else if (parts[1] == "STATUS" && data.AuthenticatedUser != null && parts.Count > 2) // list information about a mailbox
                {
                    var mailbox = parts[2];
                    var items = new List<ImapItem>().AsQueryable();
                    if (ImapItemsHandler != null)
                        items = ImapItemsHandler(data, mailbox);

                    var tags = "MESSAGES UNSEEN UIDNEXT"; // default
                    if (parts.Count > 3 && !string.IsNullOrEmpty(parts[3]))
                        tags = parts[3];
                    var resp = "";
                    foreach (var t in tags.Split(' '))
                    {
                        var space = resp.Length == 0 ? "" : " ";

                        if (t == "MESSAGES")
                            resp += space + t + " " + items.Count();
                        else if (t == "UNSEEN")
                            resp += space + t + " " + items.Where(a => !a.Seen).Count();
                        else if (t == "UIDNEXT")
                            resp += space + t + " " + (items.FirstOrDefault()?.UID ?? 0);
                        else if (t == "RECENT")
                            resp += space + t + " " + items.Where(a => a.Moment > DateTime.UtcNow.AddDays(-3)).Count();
                        else if (t == "UIDVALIDITY")
                            resp += space + t + " " + (items.FirstOrDefault()?.UID ?? 0);
                    }
                    await client.SendString("* STATUS " + mailbox + " (" + resp + ")\r\n");

                    // List status of the current selected mailbox                                        
                    await client.SendString(tag + " OK STATUS completed\r\n");
                }
                else if (parts[1] == "EXPUNGE" && data.AuthenticatedUser != null && data.SelectedMailbox != null)
                {
                    // Directly delete all items marked for deletion
                    // Send a line for each deleted item
                    // * 5 EXPUNGE
                    await client.SendString(tag + " OK EXPUNGE completed\r\n");
                }
                else if (parts[1] == "CLOSE" && data.AuthenticatedUser != null && data.SelectedMailbox != null)
                {
                    // Deselect mailbox, delete all items marked for deletion
                    await client.SendString(tag + " OK CLOSE completed\r\n");
                }
                else if (parts[1] == "FETCH" && data.AuthenticatedUser != null && parts.Count > 2) // select [sequence set]
                {
                    var tags = "FLAGS UID";
                    var sequence = parts[2];
                    if (parts.Count > 3)
                        tags = parts[3];
                    var tagsSplitted = tags.Split(' ').Distinct().ToList();

                    // TODO, parse sequence set
                    var items = new List<ImapItem>().AsQueryable();
                    if (ImapItemsHandler != null)
                        items = ImapItemsHandler(data, data.SelectedMailbox);

                    foreach (var item in items)
                    {
                        var resp = "UID " + item.UID; // always include UID

                        foreach (var t in tagsSplitted)
                        {
                            if (t == "UID")
                                continue;
                            if (t == "FLAGS")
                            {
                                resp += " FLAGS (" +
                                    (item.Seen ? "\\Seen " : "") +
                                    (item.Draft ? "\\Draft " : "") +
                                    (item.Answered ? "\\Answered " : "") +
                                    (item.Deleted ? "\\Deleted " : "") +
                                    ")";
                            }
                            else if (t == "RFC822.SIZE")
                                resp += " RFC822.SIZE " + item.Size;
                            else if (t == "INTERNALDATE")
                                resp += " INTERNALDATE \"" + item.Moment.ToString("yyyy-MM-dd HH:mm:ss") + "\"";
                            else if (t.StartsWith("BODY.PEEK[") && t.EndsWith("]"))
                            {
                                var betweenBrackets = SplitLineIntoParts(t.Substring(10, t.Length - 11));

                                resp += " BODY.PEEK[]"; // TODO
                            }
                        }

                        await client.SendString("* " + item.UID + " FETCH ("+resp+")\r\n");
                    }

                    // UID fetch 1:* (FLAGS)
                    // UID fetch 1 (UID RFC822.SIZE FLAGS BODY.PEEK[HEADER.FIELDS (From To Cc Bcc Subject Date Message-ID Priority X-Priority References Newsgroups In-Reply-To Content-Type Reply-To)])

                    // The FETCH command retrieves data associated with a message in the mailbox.The data items to be fetched can be either a single atom or a parenthesized list.
                    
                    
                    await client.SendString(tag + " OK FETCH completed\r\n");
                }
                else if (parts[1] == "STORE" && data.AuthenticatedUser != null) 
                {
                    // Modify an existing message
                }
                else if (parts[1] == "APPEND" && data.AuthenticatedUser != null) 
                {
                    // Create a new message in the mailbox
                }
                else if (parts[1] == "UID" && parts.Count >= 3 && data.AuthenticatedUser != null)
                {
                    // Can be used with COPY/FETCH/STORE, but uses unique ids instead of sequence numbers
                    await client.SendString(tag + " OK UID " + parts[2] + "completed\r\n");
                }
                else if (parts[1] == "SUBSCRIBE" && data.AuthenticatedUser != null) // subscribe [mailboxname]
                {
                    await client.SendString(tag + " OK SUBSCRIBE completed\r\n");
                }
                else if (parts[1] == "UNSUBSCRIBE" && data.AuthenticatedUser != null) // unsubscribe [mailboxname]
                {
                    await client.SendString(tag + " OK UNSUBSCRIBE completed\r\n");
                }
                else if (parts[1] == "LSUB" && data.AuthenticatedUser != null) // list all subscriptions
                {
                    await client.SendString(tag + " OK LSUB completed\r\n");

                }
                else if (parts[1] == "CHECK" && data.AuthenticatedUser != null)
                {
                    // Usually same as NOOP, but we don't have to send any updates
                    // Can be seen as a 'flush any changes to disk' command
                    await client.SendString(tag + " OK CHECK Completed\r\n");
                }
                else if (parts[1] == "NOOP" && data.AuthenticatedUser != null)
                {
                    // If there are new message updates, send them now
                    await client.SendString(tag + " OK NOOB\r\n");
                }
                else if (parts[1] == "LOGOUT" && data.AuthenticatedUser != null)
                {
                    data.AuthenticatedUser = null;
                    await client.SendString(tag + " OK LOGOUT completed\r\n");
                }
                else
                {
                    Console.WriteLine("Unknown command, StreamIsEncrypted: " + client.StreamIsEncrypted);
                    await client.SendString(tag + " BAD Incorrect syntax or unsupported command\r\n");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(ImapHandler), "Error handling imap command: " + ex.Message);

            }
        }

        public class ImapClientData
        {
            public required Client Client { get; set; }
            public required byte[] IncomingBuffer { get; set; }
            public int IncomingBufferLength { get; set; }
            public string? AuthenticatedUser { get; set; } = null;
            public string? SelectedMailbox { get; set; } = null;
        }
        public class ImapItem
        {
            // Main properties and flags
            public long UID { get; set; }
            public long Size { get; set; }
            public bool Answered { get; set; }
            public bool Seen { get; set; }
            public bool Deleted { get; set; }
            public bool Draft { get; set; }
            public DateTime Moment { get; set; }

            // Properties from the EML file
            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        }
    }
}
