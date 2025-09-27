using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    public class ModelInfo
    {
        public required string Name { get; set; }
        public required string ApiUrlCompletions { get; set; }
        public required string ApiKey { get; set; }
        public double CostPromptToken { get; set; }
        public double CostCompletionToken { get; set; }
        public ModelType Type { get; set; } = ModelType.LlamaCpp;

        public enum ModelType
        {
            LlamaCpp = 1,
            OpenAI = 2,
            AzureOpenAI = 3,
            
        }
    }
}
