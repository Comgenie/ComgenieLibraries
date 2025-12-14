using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Comgenie.AI.Entities
{
    public class ChatUserMessage : ChatMessage
    {
        public ChatUserMessage()
        {
            role = "user";
        }
        public ChatUserMessage(string message)
        {
            this.role = "user";
            this.content.Add(new ChatMessageTextContent(message));
        }
        public List<ChatMessageContent> content { get; set; } = new();
    }


    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(ChatMessageTextContent), typeDiscriminator: "text")]
    [JsonDerivedType(typeof(ChatMessageImageContent), typeDiscriminator: "image_url")]
    public abstract class ChatMessageContent
    {
        public string type { get; set; } = "text";
    }
    public class ChatMessageTextContent : ChatMessageContent
    {
        public ChatMessageTextContent()
        {
            type = "text";
        }
        [SetsRequiredMembers]
        public ChatMessageTextContent(string text)
        {
            type = "text";
            this.text = text;
        }
        public required string text { get; set; } = "";
    }

    public class ChatMessageImageContent : ChatMessageContent
    {
        public ChatMessageImageContent()
        {
            type = "image_url";
        }
        [SetsRequiredMembers]
        public ChatMessageImageContent(string localImagePath)
        {
            type = "image_url";
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
        public required ChatMessageUrl image_url { get; set; }
    }
    public class ChatMessageUrl
    {
        public required string url { get; set; }
    }
}
