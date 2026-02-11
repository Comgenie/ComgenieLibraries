using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Comgenie.AI.Entities
{
    /// <summary>
    /// A chat message for the 'user' role with content including text and/or images.
    /// </summary>
    public class ChatUserMessage : ChatMessage
    {
        /// <summary>
        /// Create a new empty chat user message instance without any added content
        /// </summary>
        public ChatUserMessage()
        {
        }

        /// <summary>
        /// Create a new chat user message instance with the given message as text content
        /// </summary>
        /// <param name="message"></param>
        public ChatUserMessage(string message)
        {
            this.content.Add(new ChatMessageTextContent(message));
        }

        /// <summary>
        /// List of content attached to this chat user message
        /// </summary>
        public List<ChatMessageContent> content { get; set; } = new();

        /// <summary>
        /// Get the content object of the given content type ChatMessageTextContent / ChatMessageImageContent
        /// or create a new one when it's not found and return that one instead.
        /// </summary>
        /// <typeparam name="T">Chat message content type</typeparam>
        /// <returns>The existing or a newly created content object</returns>
        public T GetOrCreateContent<T>() where T : ChatMessageContent, new()
        {
            var existing = (T?)content.FirstOrDefault(a => a is T);
            if (existing == null)
            {
                existing = new T();
                content.Add(existing);
            }
            return existing;
        }
    }


    /// <summary>
    /// Abstract class for content classes which can be stored within chat user messages.
    /// </summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(ChatMessageTextContent), typeDiscriminator: "text")]
    [JsonDerivedType(typeof(ChatMessageImageContent), typeDiscriminator: "image_url")]
    public abstract class ChatMessageContent
    {
    }

    /// <summary>
    /// A content object containing text content data
    /// </summary>
    public class ChatMessageTextContent : ChatMessageContent
    {
        /// <summary>
        /// Create an empty text content object containing an empty string)
        /// </summary>
        public ChatMessageTextContent()
        {
        }
        
        /// <summary>
        /// Create a text content object containing the given text
        /// </summary>
        /// <param name="text">Text to store within this text content object</param>
        public ChatMessageTextContent(string text)
        {
            this.text = text;
        }

        /// <summary>
        /// Text stored within this text content object
        /// </summary>
        public string text { get; set; } = "";
    }

    /// <summary>
    /// A content object to store image data 
    /// </summary>
    public class ChatMessageImageContent : ChatMessageContent
    {
        /// <summary>
        /// Create an empty image content object
        /// </summary>
        public ChatMessageImageContent()
        {
        }

        /// <summary>
        /// Create an image content object with a file path to a local image file.
        /// </summary>
        /// <param name="localImagePath">Path to a stored image</param>
        /// <exception cref="FileNotFoundException">An exception will be thrown if the referenced image could not be found</exception>
        public ChatMessageImageContent(string localImagePath)
        {
            // Retrieve local image and base64 encode it
            if (!File.Exists(localImagePath))
                throw new FileNotFoundException("Image file not found", localImagePath);

            var ext = Path.GetExtension(localImagePath)?.ToLower() ?? "jpg";
            if (ext.StartsWith("."))
                ext = ext.Substring(1);

            image_url = new ChatMessageUrl()
            {
                url = "data:image/" + ext + ";base64," + Convert.ToBase64String(File.ReadAllBytes(localImagePath))
            };
        }

        /// <summary>
        /// An url object containing the url to an image. The url can be a data:image/...  url.
        /// </summary>
        public ChatMessageUrl? image_url { get; set; }
    }

    /// <summary>
    /// Object to store an url
    /// </summary>
    public class ChatMessageUrl
    {
        /// <summary>
        /// Text containing the url
        /// </summary>
        public required string url { get; set; }
    }
}
