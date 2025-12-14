using Comgenie.AI.Entities;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comgenie.AI
{
    public partial class LLM
    {
        private ModelInfo ActiveModel { get; set; }
        public double CostThisSession { get; internal set; } = 0.0;
        public int Attempts { get; set; } = 3;

        /// <summary>
        /// When set, the user/assistant messages will automatically be trimmed to make this fit.
        /// Note: The trimming algorithm will count characters instead of tokens so this will usually be on the very safe side and not utilizing the full context length.
        /// </summary>
        public long? MaxContentLength { get; set; }


        /// <summary>
        /// When set to true a new request will be sent to the LLM after a tool call is made including the tool call response, allowing the LLM to continue its response based on the tool call result.
        /// </summary>
        public bool EnableContinuationAfterToolCall { get; set; } = true;

        // Automatic throttling
        public int MaxRequestsPerSecond { get; set; } = 10;
        public int MaxRequestsPerMinute { get; set; } = 600;

        private DateTime LastRequest { get; set; }
        private int CurrentRequestsSecond { get; set; } = 0;
        private int CurrentRequestsMinute { get; set; } = 0;


        public LLM(ModelInfo model, bool automaticModelInformationRetrieval = true)
        {
            ActiveModel = model;

            if (automaticModelInformationRetrieval)
                SetActiveModel(model).Wait(); // Sadly, it's not possible to have async constructors atm
        }
        public async Task SetActiveModel(ModelInfo model)
        {
            ActiveModel = model;

            if (model.ServerType == ModelInfo.ModelServerType.LlamaCpp)
            {
                var url = new Uri(model.ApiUrlCompletions);
                var httpClient = GetHttpClient();
                
                var settings = await httpClient.GetFromJsonAsync<LlamaCppProps>(url.Scheme + "://"+ url.Authority + "/props");
                if (settings != null)
                {
                    // Set limits based on current model/server info
                    if (settings.default_generation_settings.n_ctx.HasValue && settings.default_generation_settings.n_ctx.Value > 0)
                        MaxContentLength = settings.default_generation_settings.n_ctx.Value;
                }
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
            httpClient.Timeout = new TimeSpan(0, 2, 0); // 4 hours
            return httpClient;
        }

        private async Task<T> ExecuteAndRetryHttpRequestIfFailed<T>(Func<HttpClient, Task<T>> executionHandler)
        {
            var httpClient = GetHttpClient();
            for (var i = 0; i < Attempts; i++)
            {
                if (i > 0)
                    await Task.Delay(5000 * i); // Exponential backoff

                try
                {
                    var result = await executionHandler(httpClient);
                    return result;

                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to execute HTTP request: " + ex.ToString() + ", Attempt " + (i + 1) + "/" + Attempts);
                    if (i + 1 == Attempts)
                        throw;
                }
            }
            return default(T)!;
        }
    }
}
