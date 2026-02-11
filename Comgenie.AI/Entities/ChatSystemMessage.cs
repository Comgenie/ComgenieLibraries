namespace Comgenie.AI.Entities
{
    /// <summary>
    /// A chat system message object to store the system prompt message. This should be the first message to put within a chat message array to pass to the LLM.
    /// This message will not be modified and will not be automatically trimmed if the context size gets exceeded.
    /// </summary>
    public class ChatSystemMessage : ChatMessage
    {
        /// <summary>
        /// Create a new chat system message object with an empty text
        /// </summary>
        public ChatSystemMessage()
        {
        }

        /// <summary>
        /// Create a new chat system message with the given system prompt
        /// </summary>
        /// <param name="systemPrompt"></param>
        public ChatSystemMessage(string systemPrompt)
        {
            content = systemPrompt;
        }

        /// <summary>
        /// System prompt text
        /// </summary>
        public string content { get; set; } = "";
    }
}
