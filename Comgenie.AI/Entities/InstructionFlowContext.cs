using System.Text.Json.Serialization;

namespace Comgenie.AI.Entities
{
    public class InstructionFlowContext
    {
        internal List<InstructionFlowPositionContext> FlowPositions { get; set; } = new();

        [JsonIgnore]
        internal InstructionFlowPositionContext Current => FlowPositions.Last();

        public ChatResponse? LastChatResponse { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
        public bool Completed { get; set; }
    }
}
