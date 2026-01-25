namespace Comgenie.AI.Entities
{
    public class ChatSystemMessage : ChatMessage
    {
        public ChatSystemMessage()
        {
        }
        public ChatSystemMessage(string systemPrompt)
        {
            content = systemPrompt;
        }
        public string content { get; set; } = "";
    }
}
