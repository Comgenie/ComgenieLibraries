using Comgenie.AI.Entities;
using Comgenie.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Comgenie.AI
{
    public partial class LLM
    {
        /// <summary>
        /// Do a call to the embeddings endpoint to generate embeddings for the given text.
        /// Note that the number of floats returned depends on the model used. 
        /// When using llama-server, make sure the --embeddings and --pooling arguments are set, recommended arguments: --embeddings --pooling mean 
        /// </summary>
        /// <param name="text">Text to get embeddings from</param>
        /// <param name="generationOptions">Optional: Custom generation options. If left empty the DefaultGenerationOptions are used</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel the calls to the embeddings endpoint</param>
        /// <returns>Float array representing the embedding</returns>
        public async Task<float[]> GenerateEmbeddingsAsync(string text, LLMGenerationOptions? generationOptions = null, CancellationToken? cancellationToken=null)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            var embeddingsRequest = new
            {
                input = text,
                model = ActiveModel.Name,
                encoding_format = "float"
            };

            var txtContent = JsonSerializer.Serialize(embeddingsRequest, new JsonSerializerOptions()
            {
                //WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            var content = new StringContent(txtContent, Encoding.UTF8, "application/json");

            var deserialized = await ExecuteAndRetryHttpRequestIfFailedAsync<EmbeddingsResponse>(async (httpClient, requestCancellationToken) =>
            {
                var resp = await httpClient.PostAsync(ActiveModel.ApiUrlEmbeddings, content, requestCancellationToken);
                resp.EnsureSuccessStatusCode();

                var str = await resp.Content.ReadAsStringAsync(requestCancellationToken);
                if (!str.StartsWith("{"))
                    throw new Exception("Invalid LLM response: " + str);

                var embeddingsResponse = JsonSerializer.Deserialize<EmbeddingsResponse>(str);
                if (embeddingsResponse == null)
                    throw new Exception("Failed to deserialize LLM response: " + str);

                return embeddingsResponse;
            }, generationOptions, cancellationToken);
            return deserialized!.data.First().embedding;
            // {"model":"llama-cpp","object":"list","usage":{"prompt_tokens":6,"total_tokens":6},"data":[{"embedding":[0.007257496938109398,0.001204345258884132, 
        }

        /// <summary>
        /// This uses the llama-server reranking feature to rank the given documents based on their relevance to the given text.
        /// Make sure to have the --reranking flag enabled in llama-server. Recommended arguments: --embeddings --pooling rank --reranking
        /// 
        /// Note that it's recommended to use this in combination with embeddings to first filter the documents to a smaller set before reranking.
        /// The normal embbeddings method just find distances to specific words, while reranking looks at the actual relevance of the document as a whole. 
        /// </summary>
        /// <param name="text">The text to find relevance from</param>
        /// <param name="items">List of documents/items to rank. Note that the .ToString() method will be called for each of these items to find out what text it represents.</param>
        /// <param name="top_n">Max number of results to return, sorted by highest ranked</param>
        /// <param name="relevanceThreshold">Anything lower than this number will be filtered out (0 no relavance at all, 1 exactly the same)</param>
        /// <param name="generationOptions">Optional: Custom generation options to use instead of the default ones</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel the calls to the reranking endpoint</param>
        /// <returns>List of scored items with their relevance score</returns>
        public async Task<List<ScoredItem<T>>> GenerateRankingsAsync<T>(string text, List<T> items, int top_n=10, double relevanceThreshold = 0.75, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (string.IsNullOrEmpty(ActiveModel.ApiUrlReranking))
            {
                Debug.WriteLine("[Warning] No reranking url is set so falling back to pure embeddings relevance.");
                
                return items.Take(top_n).Select(a=> new ScoredItem<T>() { Item = a, Score = 1 }).ToList();
            }

            // TODO: Split requests exceeding x tokens across multiple requests and combine them using the score.

            var embeddingsRequest = new
            {
                query = text,
                model = ActiveModel.Name,
                documents = items.Select(a=>a.ToString()),
                top_n = top_n
            };

            var txtContent = JsonSerializer.Serialize(embeddingsRequest, new JsonSerializerOptions()
            {
                //WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            var content = new StringContent(txtContent, Encoding.UTF8, "application/json");

            var results = await ExecuteAndRetryHttpRequestIfFailedAsync(async (httpClient, requestCancellationToken) =>
            {
                var resp = await httpClient.PostAsync(ActiveModel.ApiUrlReranking, content, requestCancellationToken);
                resp.EnsureSuccessStatusCode();

                var str = await resp.Content.ReadAsStringAsync(requestCancellationToken);
                if (!str.StartsWith("{"))
                    throw new Exception("Invalid LLM response: " + str);
                Console.WriteLine(str);

                
                var rerankingResponse = JsonSerializer.Deserialize<RerankingResponse>(str);
                if (rerankingResponse == null)
                    throw new Exception("Failed to deserialize LLM response: " + str);

                var results = new List<ScoredItem<T>>();
                foreach (var result in rerankingResponse.results)
                {
                    results.Add(new ScoredItem<T>() {
                        Item = items[result.index],
                        Score = result.relevance_score
                    });
                }
                return results.Where(a=> a.Score >= relevanceThreshold).OrderByDescending(a => a.Score).ToList();
            }, generationOptions, cancellationToken);
            
            return results;
        }


        /// <summary>
        /// This uses the llama-server reranking feature to rank the given documents based on their relevance to the given text.
        /// Make sure to have the --reranking flag enabled in llama-server. Recommended arguments: --embeddings --pooling rank --reranking
        /// 
        /// Note that it's recommended to use this in combination with embeddings to first filter the documents to a smaller set before reranking.
        /// The normal embbeddings method just find distances to specific words, while reranking looks at the actual relevance of the document as a whole. 
        /// </summary>
        /// <param name="text">The text to find relevance from</param>
        /// <param name="documents">List of documents (text) to rank.</param>
        /// <param name="top_n">Max number of results to return, sorted by highest ranked</param>
        /// <param name="relevanceThreshold">Anything lower than this number will be filtered out (0 no relavance at all, 1 exactly the same)</param>
        /// <param name="generationOptions">Optional: Custom generation options to use instead of the default ones</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel the calls to the reranking endpoint</param>
        /// <returns>List of scored items with their relevance score</returns>
        public Task<List<ScoredItem<string>>> GenerateRankingsAsync(string text, List<string> documents, int top_n = 10, double relevanceThreshold = 0.75, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            return GenerateRankingsAsync<string>(text, documents, top_n, relevanceThreshold, generationOptions, cancellationToken);
        }
    }
}
