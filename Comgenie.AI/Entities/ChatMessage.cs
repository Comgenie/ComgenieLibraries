using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Comgenie.AI.Entities
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "role", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(ChatToolMessage), typeDiscriminator: "tool")]
    [JsonDerivedType(typeof(ChatUserMessage), typeDiscriminator: "user")]
    [JsonDerivedType(typeof(ChatSystemMessage), typeDiscriminator: "system")]
    [JsonDerivedType(typeof(ChatAssistantMessage), typeDiscriminator: "assistant")]
    public abstract class ChatMessage
    {
        public ChatMessage Clone()
        {
            // Use serialization to create a deep copy of the object
            var json = JsonSerializer.Serialize(this);
            return (ChatMessage)JsonSerializer.Deserialize(json, this.GetType())!;
        }
    }


}
