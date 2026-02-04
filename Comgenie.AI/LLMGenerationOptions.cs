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

        /// <summary>
        /// If the cache is configured and this property is set to true, then the cache will first be checked to see if there is already a response available.
        /// </summary>
        public bool UseCacheIfAvailable { get; set; } = true;

        public DocumentReferencingMode DocumentReferencingMode { get; set; } = DocumentReferencingMode.FunctionCallDocuments;
        public int DocumentReferencingCombineCloseCharacterCount { get; set; } = 50;
        public int DocumentReferencingExpandBeforeCharacterCount { get; set; } = 50;
        public int DocumentReferencingExpandAfterCharacterCount { get; set; } = 50;
        public int DocumentReferencingMaxSize { get; set; } = 1024;
        public string DocumentReferencingXMLDocumentsTagName { get; set; } = "documents";
        public string DocumentReferencingXMLDocumentTagName { get; set; } = "document";
        public string DocumentReferencingAddedInstruction { get; set; } = "Here are the related passages in the attached documents based on the user's last message";
        public double DocumentReferencingRelevanceThreshold { get; set; } = 0.75;

        /// <summary>
        /// When set, any text used to find related documents will be passed through this function and it's return value will be used instead.
        /// Use this to strip out any injected tags or text to improve finding related documents.
        /// If returning a null or empty string, the related documents finder will stop early and return 0 results.
        /// </summary>
        public Func<string, string>? OnDocumentRelatedText { get; set; }

        /// <summary>
        /// When set, this action will be used as first attempt to trim chat messages to make it fit into the model's context size.
        /// This will only be called when the messages are exceeding the context size.
        /// The default method (delete old user/assistant messages) will be used when this method is null or doesn't reduce the size at all.
        /// </summary>
        public Action<List<ChatMessage>, long>? OnTrimMessages { get; set; } = null;


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
                ContinueAfterToolCalls = ContinueAfterToolCalls,
                FailedRequestRetryCount = FailedRequestRetryCount,
                UseCacheIfAvailable = UseCacheIfAvailable,
                DocumentReferencingMode = DocumentReferencingMode,
                DocumentReferencingAddedInstruction = DocumentReferencingAddedInstruction,
                DocumentReferencingMaxSize = DocumentReferencingMaxSize,
                DocumentReferencingCombineCloseCharacterCount = DocumentReferencingCombineCloseCharacterCount,
                DocumentReferencingExpandAfterCharacterCount = DocumentReferencingExpandAfterCharacterCount,
                DocumentReferencingXMLDocumentsTagName = DocumentReferencingXMLDocumentsTagName,
                DocumentReferencingExpandBeforeCharacterCount = DocumentReferencingExpandBeforeCharacterCount,
                DocumentReferencingRelevanceThreshold = DocumentReferencingRelevanceThreshold,
                DocumentReferencingXMLDocumentTagName = DocumentReferencingXMLDocumentTagName,
                OnDocumentRelatedText = OnDocumentRelatedText,
                OnTrimMessages = OnTrimMessages

            };
        }
    }
}
