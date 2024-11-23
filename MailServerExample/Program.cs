using Comgenie.Server;
using Comgenie.Server.Handlers.Imap;
using Comgenie.Server.Handlers.Smtp;
using System.Runtime.InteropServices;

namespace MailServerExample
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Note: the Imap handler is currently still in development.");

            // Hardcoded test data
            var domain = "testdomain.com";
            var users = new Dictionary<string, string>()
            {
                { $"testuser@{domain}", "TestPassword123" }
            };

            var mailItems = new Dictionary<string, List<ImapHandler.ImapItem>>()
            {
                { 
                    "INBOX",
                    new List<ImapHandler.ImapItem>()
                    {
                        new ImapHandler.ImapItem()
                        {
                            UID = 1,
                            Answered = false,
                            Seen = false,
                            Deleted = false,
                            Size = 1000,
                            Moment = DateTime.UtcNow,
                            Headers =
                            {
                                { "Subject", "Hi there!" },
                                { "To", $"testuser@{domain}" },
                            }
                        }
                    }
                }
            };

            using (var server = new Server())
            {
                server.AddDomain(domain);

                // IMAP for viewing received emails
                var imap = new ImapHandler();
                imap.SetAuthenticationCheckCallback((clientData, username, password) =>
                {
                    return users.ContainsKey(username) && users[username] == password;
                });

                imap.SetMailboxActionCallBack((clientData, action, mailboxName, newMailboxName) =>
                {
                    // Do mailbox actions here
                });

                imap.SetImapGetContentCallBack((clientData, uid) =>
                {
                    var testEml = "Subject: Hi there!\r\n\r\nEmail contents goes here";
                    return new MemoryStream(System.Text.Encoding.ASCII.GetBytes(testEml));
                });

                imap.SetListMailboxesCallBack((clientData) =>
                {
                    return mailItems.Keys.ToList();
                });

                imap.SetListItemsCallBack((clientData, mailboxName) =>
                {
                    if (mailItems.ContainsKey(mailboxName))
                        return mailItems[mailboxName].AsQueryable();
                    return new List<ImapHandler.ImapItem>().AsQueryable();
                });
                
                server.Listen(143, false, imap);
                server.Listen(993, true, imap);


                // SMTP for receiving and sending emails
                var smtp = new SmtpHandler();
                smtp.SetAuthenticationCheckCallBack((clientData, username, password) =>
                {
                    return users.ContainsKey(username) && users[username] == password;
                });
                smtp.SetMailboxCheckCallBack((clientData, mailbox) =>
                {
                    if (clientData.IsAuthenticated)
                        return true; // Relay is allowed
                    return users.ContainsKey(mailbox);
                });

                smtp.SetIncomingEmailCallBack(clientData =>
                {
                    foreach (var rcptTo in clientData.RcptTo)
                    {
                        if (users.ContainsKey(rcptTo))
                        {
                            // Store email in mailbox
                        }
                        else
                        {
                            // Relay                            
                        }
                    }                    
                });
                server.Listen(25, false, smtp);

                Console.WriteLine("Imap server is running on localhost:143 and localhost:993");
                Console.WriteLine("SMTP server is running on localhost:25");
                Console.ReadLine();
            }
        }
    }
}