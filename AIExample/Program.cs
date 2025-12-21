using Comgenie.AI;
using Comgenie.AI.Entities;
using System.Reflection;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace AIExample
{
    internal class Program
    {
        
        static void Main(string[] args)
        {
            var model = new ModelInfo()
            {
                Name = "llama-cpp",
                ApiKey = "",
                ApiUrlCompletions = "http://127.0.0.1:8080/v1/chat/completions",
                ApiUrlEmbeddings = "http://127.0.0.1:8081/v1/embeddings",
                ApiUrlReranking = "http://127.0.0.1:8082/v1/reranking",
                CostCompletionToken = 0, // Cost per token for completion
                CostPromptToken = 0 // Cost per token for prompt
            };

            BasicExamples.NormalResponseExample(model).Wait();
            BasicExamples.StructuredResponseExample(model).Wait();

            DocumentSearchExamples.DocumentExample(model).Wait(); // Requires Embedding and Ranking url to be set
            DocumentSearchExamples.EmbeddingsExample(model).Wait(); // Requires Embedding and Ranking url to be set

            ToolCallExamples.ToolCallExample(model).Wait();

            //ScriptExamples.ScriptExample(model).Wait();

            FlowExamples.FlowExample(model).Wait();
            FlowExamples.RepeatableFlowExample(model).Wait();
            FlowExamples.MultipleFlowExample(model).Wait();
            
            AgentExamples.AgentExample(model).Wait();
        }
    }
}
