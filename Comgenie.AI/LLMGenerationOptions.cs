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
        /// A balanced 0.7 is set as default.
        /// </summary>
        public float Temperature { get; set; } = 0.7f;

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
        /// How many times to retry a failed request to the LLM endpoint. Both server errors as unexpected (empty/incorrect) responses are seen as failed attempts.
        /// </summary>
        public int FailedRequestRetryCount { get; set; } = 3;

        public bool UseCacheIfAvailable { get; set; } = true;

        public DocumentReferencingMode DocumentReferencingMode { get; set; } = DocumentReferencingMode.FunctionCallDocuments;
        public int DocumentReferencingCombineCloseCharacterCount { get; set; } = 50;
        public int DocumentReferencingExpandBeforeCharacterCount { get; set; } = 50;
        public int DocumentReferencingExpandAfterCharacterCount { get; set; } = 50;
        public int DocumentReferencingMaxSize { get; set; } = 1024;

        /// <summary>
        /// Create a copy of this options instance so that all settings can be changed safely.
        /// </summary>
        /// <returns>A new instance containing the same option values</returns>
        public LLMGenerationOptions Clone()
        {
            return new LLMGenerationOptions
            {
                Temperature = Temperature,
                AddResponseToMessageList = AddResponseToMessageList,
                StopEarlyTextSequences = StopEarlyTextSequences,
                IncludeAvailableTools = IncludeAvailableTools,
                ExecuteToolCalls = ExecuteToolCalls,
                ContinueAfterToolCalls = ContinueAfterToolCalls
            };
        }
    }
}
