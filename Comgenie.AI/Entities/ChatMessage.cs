using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
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
    }


}
