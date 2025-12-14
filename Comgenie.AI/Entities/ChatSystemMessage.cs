namespace Comgenie.AI.Entities
{
    public class ChatSystemMessage : ChatMessage
    {
        public ChatSystemMessage()
        {
            role = "system";
        }
        public ChatSystemMessage(string systemPrompt)
        {
            role = "system";
            content = systemPrompt;
        }
        public string content { get; set; } = "";
    }
}
