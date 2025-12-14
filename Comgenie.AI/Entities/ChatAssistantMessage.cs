namespace Comgenie.AI.Entities
{
    public class ChatAssistantMessage : ChatMessage
    {
        public ChatAssistantMessage()
        {
            role = "assistant";
        }
        public string content { get; set; } = "";
        public List<ChatAssistantMessageToolCall>? tool_calls { get; set; }
    }
    public class ChatAssistantMessageToolCall
    {
        public string type { get; set; }
        public ChatAssistantMessageToolCallFunction? function { get; set; }
    }
    public class ChatAssistantMessageToolCallFunction
    {
        public string name { get; set; }
        public string arguments { get; set; }
        public string id { get; set; }

        //public ToolCallArguments arguments { get; set; }

        // "tool_calls":[{"type":"function","function":{"name":"MakeMagicHappen","arguments":"{\"magicQuestion\":\"What is the most magical thing about this photo?\",\"magicMultiplier\":5}"},"id":"suJN4Lu1UjGgWXMhvj24FFLJqwfRF18h"}]
    }
}
