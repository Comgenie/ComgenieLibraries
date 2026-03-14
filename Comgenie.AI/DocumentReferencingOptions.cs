using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Comgenie.AI.LLM;

namespace Comgenie.AI
{
    /// <summary>
    /// Options for referencing documents.
    /// </summary>
    public class DocumentReferencingOptions
    {
        /// <summary>
        /// Control the format used when referencing documents
        /// </summary>
        public DocumentReferencingFormat Format { get; set; } = DocumentReferencingFormat.XML;

        /// <summary>
        /// If two or more texts are found from the same document, but are located within this amount of non-included characters within each other
        /// then it will be combined and given to the llm as one text.
        /// </summary>
        public int CombineCloseCharacterCount { get; set; } = 50;

        /// <summary>
        /// If a relevant text is found, it will also include this amount of characters before that text even if it's not that relevant but still useful for context
        /// </summary>
        public int ExpandBeforeCharacterCount { get; set; } = 50;

        /// <summary>
        /// If a relevant text is found, it will also include this amount of characters after that text even if it's not that relevant but still useful for context
        /// </summary>
        public int ExpandAfterCharacterCount { get; set; } = 50;

        /// <summary>
        /// Max size (including xml or json tags) of the text referencing any found relevant documents/texts.
        /// The most relevant texts will always be at the top so less relevant texts will be ommited if it doesn't fit within this max size.
        /// </summary>
        public int MaxSize { get; set; } = 1024;

        /// <summary>
        /// When the referencing format is set to XML, this tag name will be used to contain the list.
        /// By default: documents. But useful to change if you are referencing other things (code, memories, etc.)
        /// </summary>
        public string XMLDocumentsTagName { get; set; } = "documents";

        /// <summary>
        /// When the referencing format is set to XML, this tag name will be used to contain the list item.
        /// By default: document. But useful to change if you are referencing other things (code, memories, etc.)
        /// </summary>
        public string XMLDocumentTagName { get; set; } = "document";

        /// <summary>
        /// When using the non-function call referencing modes. This text will be placed above any referenced document texts.
        /// It will not be placed if there wasn't any relevant document texts.
        /// </summary>
        public string AddedInstruction { get; set; } = "Here are the related passages in the attached documents based on the user's last message";

        /// <summary>
        /// The reranking endpoint will give a score to each of the relevant texts found within the embeddings.
        /// Use this to ommit any results giving a low relevance score.
        /// </summary>
        public double RelevanceThreshold { get; set; } = 0.75;

        /// <summary>
        /// When set, any text used to find related documents will be passed through this function and it's return value will be used instead.
        /// Use this to strip out any injected tags or text to improve finding related documents.
        /// If returning a null or empty string, the related documents finder will stop early and return 0 results.
        /// </summary>
        public Func<string, string>? OnDocumentRelatedText { get; set; }

    }
}
