using System.Text.Json.Serialization;

namespace Comgenie.AI.Entities
{
    internal class InstructionFlowPositionContext
    {
        [JsonIgnore]
        public required InstructionFlow Flow { get; set; }
        public string? FlowName { get; set; }
        public int CurrentStep { get; set; }
        public int NextStep { get; set; }
        public bool StopRequested { get; set; }

    }
}
