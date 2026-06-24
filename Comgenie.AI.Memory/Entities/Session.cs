using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Comgenie.AI.Memory.Entities
{
    public class Session
    {
        public required string Mode { get; set; } = "Conversation";
        public DateTime Started { get; set; } = DateTime.UtcNow;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public List<ChatMessage> Messages { get; set; } = new();
        public string Transcript { get; set; } = "";
        public TextAnalysis? Analysis { get; set; } = null;
    }
}
