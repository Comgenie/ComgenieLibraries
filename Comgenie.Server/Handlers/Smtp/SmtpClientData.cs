using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers.Smtp
{
    public class SmtpClientData
    {
        public required List<string> RcptTo { get; set; }
        public required List<string> MailBox { get; set; }
        public required byte[] IncomingBuffer { get; set; }
        public string? HeloInfo { get; set; }
        public string? MailFrom { get; set; }
        public int IncomingBufferLength { get; set; }
        public bool InDataPart { get; set; }
        public Stream? FileDataStream { get; set; }
        public string? FileName { get; set; }

        public string? SmtpAuthMethod { get; set; }
        public string? SmtpAuthUsername { get; set; }
        public string? SmtpAuthPassword { get; set; }
        public bool IsAuthenticated { get; set; }

        // Check result
        public string? DKIM_Domain { get; set; }
        public bool DKIM_Pass { get; set; }
        public string? DKIM_FailReason { get; set; }

        public string? SPF_IP { get; set; }
        public bool? SPF_Pass { get; set; }

        public string? DMARC_Action { get; set; }

    }
}
