using Comgenie.AI.Entities;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Comgenie.AI
{
    public partial class LLM
    {
        private ModelInfo ActiveModel { get; set; }
        public double CostThisSession { get; internal set; } = 0.0;
        
        public LLMGenerationOptions DefaultGenerationOptions { get; set; } = new LLMGenerationOptions();

        /// <summary>
        /// When set, the user/assistant messages will automatically be trimmed to make this fit.
        /// Note: The trimming algorithm will count characters instead of tokens so this will usually be on the very safe side and not utilizing the full context length.
        /// </summary>
        public long? MaxContentLength { get; set; }

        // Automatic throttling
        public int MaxRequestsPerSecond { get; set; } = 10;
        public int MaxRequestsPerMinute { get; set; } = 600;

        private DateTime LastRequest { get; set; }
        private int CurrentRequestsSecond { get; set; } = 0;
        private int CurrentRequestsMinute { get; set; } = 0;


        public LLM(ModelInfo model, bool automaticModelInformationRetrieval = true)
        {
            ActiveModel = model;

            SetActiveModel(model, automaticModelInformationRetrieval).Wait(); // Sadly, it's not possible to have async constructors atm
        }
        public async Task SetActiveModel(ModelInfo model, bool automaticModelInformationRetrieval = true)
        {
            ActiveModel = model;
            if (model.MaxContextLength.HasValue && model.MaxContextLength.Value > 0)
            {
                MaxContentLength = model.MaxContextLength;
            }
            else if (model.ServerType == ModelInfo.ModelServerType.LlamaCpp && automaticModelInformationRetrieval)
            {
                var url = new Uri(model.ApiUrlCompletions);
                var httpClient = GetHttpClient();
                try
                {
                    var settings = await httpClient.GetFromJsonAsync<LlamaCppProps>(url.Scheme + "://" + url.Authority + "/props");
                    if (settings != null)
                    {
                        // Set limits based on current model/server info
                        if (settings.default_generation_settings.n_ctx.HasValue && settings.default_generation_settings.n_ctx.Value > 0)
                            MaxContentLength = settings.default_generation_settings.n_ctx.Value;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Could not retrieve context length from /props endpoint. " + ex.Message);
                }
            }


            if (!MaxContentLength.HasValue)
            {
                // Set a useful default
                // - Recent models of OpenAI/Azure AI have 128k+ context length
                // - Llama.cpp models are often run locally with very limited context lengths
                MaxContentLength = model.ServerType == ModelInfo.ModelServerType.LlamaCpp ? 8_000 : 128_000;  
            }
            
        }
        private class LlamaCppProps
        {
            public required LlamaCppPropsDefaultGenerationSettings default_generation_settings { get; set; }
        }
        private class LlamaCppPropsDefaultGenerationSettings
        {
            public int? n_ctx { get; set; }
        }

        private HttpClient GetHttpClient()
        {
            var httpClient = new HttpClient();
            if (ActiveModel.ServerType == ModelInfo.ModelServerType.OpenAI)
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ActiveModel.ApiKey);
            else
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("api-key", ActiveModel.ApiKey);
            return httpClient;
        }
        
        private async Task<T> ExecuteAndRetryHttpRequestIfFailedAsync<T>(Func<HttpClient, CancellationToken, Task<T>> executionHandler, LLMGenerationOptions generationOptions, CancellationToken? cancellationToken = null)
        {
            var httpClient = GetHttpClient();
            httpClient.Timeout = generationOptions.RequestTimeout;
            for (var i = 0; i < generationOptions.FailedRequestRetryCount; i++)
            {
                if (i > 0)
                    await Task.Delay(5000 * i); // Exponential backoff
                CancellationTokenSource cts = new CancellationTokenSource();
                cts.CancelAfter(generationOptions.RequestTimeout); // Cancel after 2 minutes

                var newCancellationToken = cts.Token;

                if (cancellationToken.HasValue)
                    newCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken.Value).Token;
                try
                {
                    var result = await executionHandler(httpClient, newCancellationToken);
                    return result;

                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to execute HTTP request: " + ex.ToString() + ", Attempt " + (i + 1) + "/" + generationOptions.FailedRequestRetryCount);
                    if (i + 1 == generationOptions.FailedRequestRetryCount)
                        throw;
                }

                if (cancellationToken.HasValue)
                    cancellationToken.Value.ThrowIfCancellationRequested();
            }
            return default(T)!;
        }
    }
}
