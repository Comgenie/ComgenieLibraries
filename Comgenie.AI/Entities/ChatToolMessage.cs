namespace Comgenie.AI.Entities
{
    public class ChatToolMessage : ChatMessage
    {
        public ChatToolMessage()
        {
            role = "tool";
        }

        public string? tool_call_id { get; set; }
        public string content { get; set; } = "";
    }
}
