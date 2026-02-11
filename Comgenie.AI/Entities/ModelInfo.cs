using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI.Entities
{
    /// <summary>
    /// An object containing all information to talk to a LLM model (url's, server type)
    /// </summary>
    public class ModelInfo
    {
        /// <summary>
        /// Name of the model, will be passed to the completions endpoint, is required for non-llama-cpp endpoints
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Url of the completions endpoint accepting chat messages and returning an assistant message response.
        /// </summary>
        public required string ApiUrlCompletions { get; set; }

        /// <summary>
        /// Api key which will be passed to the completions endpoint, is required for non-llama-cpp endpoints.
        /// </summary>
        public required string ApiKey { get; set; }

        /// <summary>
        /// Server type which is hosting the model
        /// </summary>
        public ModelServerType ServerType { get; set; } = ModelServerType.LlamaCpp;

        // Required for embeddings/documents features

        /// <summary>
        /// Only required for embeddings or documents features.
        /// This is the url used for the embedings endpoint when retrieving embeddings data.
        /// </summary>
        public string? ApiUrlEmbeddings { get; set; }

        /// <summary>
        /// Very recommended for documents features.
        /// This is the url used for the reranking endpoint when retrieving relevance scores for finding relevance documents.
        /// When not set it will fall back on using pure embeddings similarity scores.
        /// Note: This one is llama-server specific.
        /// </summary>
        public string? ApiUrlReranking { get; set; }

        /// <summary>
        /// Optional: Cost per prompt token. When set it will update the LLM.CostThisSession variable after each request.
        /// </summary>
        public double CostPromptToken { get; set; } = 0;

        /// <summary>
        /// Optional: Cost per completion token. When set it will update the LLM.CostThisSession variable after each request.
        /// </summary>
        public double CostCompletionToken { get; set; } = 0;

        // Extra model information, will be retrieved automatically for Llama.cpp
        /// <summary>
        /// Max content length for the LLM. This will be used to automatically remove non-system messages at the start of large list of chat messages.
        /// For llama-server hosted models this will be retrieved automatically when left empty.
        /// </summary>
        public long? MaxContextLength { get; set; }

        /// <summary>
        /// Type of the server hosting this model. This might change how some calls are executed and how api keys are passed to them.
        /// </summary>
        public enum ModelServerType
        {
            LlamaCpp = 1,
            OpenAI = 2,
            AzureOpenAI = 3,
        }

    }
}
