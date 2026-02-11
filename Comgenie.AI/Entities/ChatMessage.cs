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
    /// <summary>
    /// Abstract class for a singular message which can be serialized and given to the LLM completions endpoint.
    /// Note: The role property will be set and used automatically based on the C# class during serializing/deserializing.
    /// </summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "role", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(ChatToolMessage), typeDiscriminator: "tool")]
    [JsonDerivedType(typeof(ChatUserMessage), typeDiscriminator: "user")]
    [JsonDerivedType(typeof(ChatSystemMessage), typeDiscriminator: "system")]
    [JsonDerivedType(typeof(ChatAssistantMessage), typeDiscriminator: "assistant")]
    public abstract class ChatMessage
    {
        /// <summary>
        /// Create a copy of this message object. This will be done by serializing and deserializing this object.
        /// </summary>
        /// <returns>The newly cloned chat message object</returns>
        public ChatMessage Clone()
        {
            // Use serialization to create a deep copy of the object
            var json = JsonSerializer.Serialize(this);
            return (ChatMessage)JsonSerializer.Deserialize(json, this.GetType())!;
        }
    }


}
