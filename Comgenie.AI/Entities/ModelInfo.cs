using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI.Entities
{
    public class ModelInfo
    {
        public required string Name { get; set; }
        public required string ApiUrlCompletions { get; set; }
        public required string ApiKey { get; set; }
        public ModelServerType ServerType { get; set; } = ModelServerType.LlamaCpp;

        // Required for embeddings/documents features
        public string? ApiUrlEmbeddings { get; set; }
        public string? ApiUrlReranking { get; set; } // llama.cpp specific

        // Optional cost tracking
        public double CostPromptToken { get; set; }
        public double CostCompletionToken { get; set; }
        

        // Extra model information, will be retrieved automatically for Llama.cpp
        public long? MaxContextLength { get; set; }


        public enum ModelServerType
        {
            LlamaCpp = 1,
            OpenAI = 2,
            AzureOpenAI = 3,
            
        }

    }
}
