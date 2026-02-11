namespace Comgenie.AI.Entities
{
    /// <summary>
    /// A tool call chat message which can be used to give a tool call result back to an LLM after execution.
    /// </summary>
    public class ChatToolMessage : ChatMessage
    {
        /// <summary>
        /// Initialize a new empty tool chat message
        /// </summary>
        public ChatToolMessage()
        {
        }

        /// <summary>
        /// Reference to the tool call given by the LLM
        /// </summary>
        public string? tool_call_id { get; set; }

        /// <summary>
        /// JSON serialized return value of the executed tool call
        /// </summary>
        public string content { get; set; } = "";
    }
}
