using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Comgenie.AI.LLM;

namespace Comgenie.AI
{
    /// <summary>
    /// Options passed to the LLM to control the generation as well as some options used by the internal code to build the request and process the response.
    /// </summary>
    public class LLMGenerationOptions
    {
        /// <summary>
        /// Temperature when generating a response from the LLM model.
        /// A lower number will keep responses very stable and focussed.
        /// A higher number 1.0 or higher will introduce more randomness and cause weirder but more creative responses.
        /// Some models only support 1.0 so that is set as default.
        /// </summary>
        public float Temperature { get; set; } = 1f;

        /// <summary>
        /// Extra parameters included in the root of the completions request.
        /// This can be used to for example include settings like min_p, repeat_penalty, presence_penalty and dry_multiplier in the quest.
        /// </summary>
        public Dictionary<string, object> ExtraRequestParameters { get; set; } = new();

        /// <summary>
        /// Request timeout for an individual completions request.
        /// By default it's set to 10 minutes.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = new(0,10,0);

        /// <summary>
        /// When set to true (default) any LLM response will be added to the list instance passed into the Generate-methods.
        /// </summary>
        public bool AddResponseToMessageList { get; set; } = true;

        /// <summary>
        /// When set to a value, the assistant response will be stopped as soon as one of these text sequences is detected instead of waiting till the model trained 'end response' token.
        /// </summary>
        public string[]? StopEarlyTextSequences { get; set; } = null;

        /// <summary>
        /// When set to true (default), the added tool calls within the executing LLM instance will be added in the generation request, making the model aware that they exists and can be called.
        /// </summary>
        public bool IncludeAvailableTools { get; set; } = true;

        /// <summary>
        /// When set to true (default), tool calls made by the LLM in their response are executed automatically
        /// </summary>
        public bool ExecuteToolCalls { get; set; } = true;

        /// <summary>
        /// When set to true (default), the response of automatic executed tool calls (ExecuteToolCalls set to true) will be directly passed back to the LLM in a new generation request.
        /// </summary>
        public bool ContinueAfterToolCalls { get; set; } = true;

        /// <summary>
        /// When set, this will limit the max amount of times the LLM will continue after executing a tool. This can be used to prevent infinite looping.
        /// </summary>
        public int? ContinueAfterToolCallsLimit { get; set; } = null;

        /// <summary>
        /// How many times to retry a failed request to the LLM endpoint. Both server errors as unexpected (empty/incorrect) responses are seen as failed attempts.
        /// </summary>
        public int FailedRequestRetryCount { get; set; } = 3;

        /// <summary>
        /// If the cache is configured and this property is set to true, then the cache will first be checked to see if there is already a response available.
        /// </summary>
        public bool UseCacheIfAvailable { get; set; } = true;

        
        /// <summary>
        /// When set, this action will be used as first attempt to trim chat messages to make it fit into the model's context size.
        /// This will only be called when the messages are exceeding the context size.
        /// The default method (delete old user/assistant messages) will be used when this method is null or doesn't reduce the size at all.
        /// </summary>
        public Action<List<ChatMessage>, long>? OnTrimMessages { get; set; } = null;

        /// <summary>
        /// When set to true (default), registered request modifiers will be executed before doing the actual request.
        /// This can be used to inject information within the prompts like document summaries.
        /// </summary>
        public bool EnableRequestModifiers { get; set; } = true;

        /// <summary>
        /// Create a copy of this options instance so that all settings can be changed safely.
        /// </summary>
        /// <param name="executeOnClonedOptions">Optionally use this to execute code with the new options. This can be used to quickly change some options)</param>
        /// <returns>A new instance containing the same option values</returns>
        public LLMGenerationOptions Clone(Action<LLMGenerationOptions>? executeOnClonedOptions = null)
        {
            var options = new LLMGenerationOptions
            {
                Temperature = Temperature,
                AddResponseToMessageList = AddResponseToMessageList,
                StopEarlyTextSequences = StopEarlyTextSequences,
                IncludeAvailableTools = IncludeAvailableTools,
                ExecuteToolCalls = ExecuteToolCalls,
                ContinueAfterToolCalls = ContinueAfterToolCalls,
                FailedRequestRetryCount = FailedRequestRetryCount,
                UseCacheIfAvailable = UseCacheIfAvailable,
                ContinueAfterToolCallsLimit = ContinueAfterToolCallsLimit,
                OnTrimMessages = OnTrimMessages,
                ExtraRequestParameters = ExtraRequestParameters.ToDictionary(a=>a.Key, a => a.Value),
                RequestTimeout = RequestTimeout,
                EnableRequestModifiers = EnableRequestModifiers
            };

            if (executeOnClonedOptions != null)
                executeOnClonedOptions(options);

            return options;
        }
    }
}
