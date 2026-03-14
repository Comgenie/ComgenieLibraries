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
        private DocumentVectorDB? DocumentVectorDB { get; set; }

        /// <summary>
        /// Add a document to the vector database by generating embeddings for it.
        /// The strategy can be controlled using the mode parameter.
        /// </summary>
        /// <param name="documentName">Name of the document to add. If a document with this name was already added before, all document and vector data will be overriden.</param>
        /// <param name="documentText">Full text representation of the document</param>
        /// <param name="mode">Strategy to create searchable embeddings for the vector database</param>
        /// <param name="documentChunkCharacterCount">Split the added document up by max this amount of characters</param>
        /// <param name="documentOverlappingCharacterCount">Add overlapping parts of the document by these amount of characters.</param>
        /// <param name="generationOptions">Optional: Options to control the LLM generation and behaviour within this method. If not set the .DefaultGenerationOptions is used.</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel the request early</param>
        /// <returns>Task as it will have to communicate with the LLM to generate embeddings.</returns>
        public async Task AddDocumentAsync(string documentName, string documentText, DocumentEmbedMode mode = DocumentEmbedMode.Overlapping, int documentChunkCharacterCount = 200, int documentOverlappingCharacterCount = 100, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (DocumentVectorDB == null)
            {
                // Generate a dummy embedding to get the dimension size
                var embedding = await GenerateEmbeddingsAsync("Dummy Text");
                DocumentVectorDB = new DocumentVectorDB(embedding.Length);
            }

            var document = DocumentVectorDB.UpsertDocumentSource(documentName, documentText);

            // Generate embeddings for the document and add it to a vector database. Automatically use this database when doing LLM requests to provide context.
            // Also support multiple embeddings techniques (sentences, overlapping, small-to-big, etc.)

            if (mode == DocumentEmbedMode.SplitSentence)
            {
                var sentences = documentText.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var index = 0;
                while (index < documentText.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var nextPeriod = documentText.IndexOf('.', index);
                    if (nextPeriod == -1)
                    {
                        nextPeriod = documentText.Length;
                    }
                    else
                    {
                        nextPeriod++; // Include the period in the current sentence
                    }

                    await GenerateEmbeddingsAndAddDocumentSectionAsync(document, index, nextPeriod - index, generationOptions, cancellationToken);
                    index = nextPeriod;
                }
            }
            else if (mode == DocumentEmbedMode.Overlapping)
            {
                int chunkSize = documentChunkCharacterCount;
                int overlapSize = documentOverlappingCharacterCount;
                for (var i=0;i<documentText.Length; i+= overlapSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var length = Math.Min(chunkSize, documentText.Length - i);
                    await GenerateEmbeddingsAndAddDocumentSectionAsync(document, i, length, generationOptions, cancellationToken);
                }
            }
            else if (mode == DocumentEmbedMode.DontEmbed)
            {
                return; // Don't embed in prompt. Used for actions like deep dive.
            }
            else
            {
                await GenerateEmbeddingsAndAddDocumentSectionAsync(document, 0, documentText.Length, generationOptions, cancellationToken);
            }
        }
        private async Task GenerateEmbeddingsAndAddDocumentSectionAsync(DocumentVectorDB.DocumentSource document, int offset, int length, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (DocumentVectorDB == null)
                throw new Exception("DocumentVectorDB is null");

            var embedding = await GenerateEmbeddingsAsync(document.Text.Substring(offset, length), generationOptions, cancellationToken);
            DocumentVectorDB.UpsertDocumentSection(document, offset, length, embedding);
        }

        /// <summary>
        /// Remove a document and all its sections from the vector database.
        /// </summary>
        /// <param name="documentName">Name of the document to remove. This method doesn't do anything if the document does not exists.</param>
        public void RemoveDocument(string documentName)
        {
            if (DocumentVectorDB == null)
                return;
            DocumentVectorDB.DeleteDocumentSource(documentName);
        }

        /// <summary>
        /// Check if a document is already added to the document vector db (with at least 1 textual reference)
        /// </summary>
        /// <param name="documentName">Name used to add the document</param>
        /// <returns>True if the document is found and has at least one reference</returns>
        public bool HasDocument(string documentName)
            => DocumentVectorDB?.Documents?.ContainsKey(documentName) == true && DocumentVectorDB.Documents[documentName].References.Count > 0;
        
        
        /// <summary>
        /// Completely unload the vector db and all documents within.
        /// </summary>
        public void ClearDocuments()
        {
            if (DocumentVectorDB == null)
                return;
            DocumentVectorDB.Dispose();
            DocumentVectorDB = null;
        }
        
        /// <summary>
        /// Enable and configure the request modifier for automatic referencing documents based on the last user message.
        /// The request modifier will be registered as 'DocumentRecall' in this LLM instance.
        /// </summary>
        /// <param name="documentReferencingOptions">Optional: Custom document referencing options where the format or any limits can be configured</param>
        /// <returns>This llm instance</returns>
        public LLM ConfigureDocumentAutomaticInclusionRequestModifier(DocumentReferencingOptions? documentReferencingOptions = null)
        {
            if (documentReferencingOptions == null)
                documentReferencingOptions = new DocumentReferencingOptions();

            // Automatic register the document recall modifier
            if (documentReferencingOptions.Format != DocumentReferencingFormat.None)
            {
                RegisterRequestModifier("DocumentRecall", async (messages, generationOptions, cancellationToken) =>
                {
                    if (DocumentVectorDB != null && messages.Last() is ChatUserMessage userMessage)
                    {
                        var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                        if (textContent != null)
                        {
                            if (documentReferencingOptions.Format != DocumentReferencingFormat.None)
                            {
                                var summary = await GenerateRelatedDocumentsSummaryAsync(textContent.text, documentReferencingOptions, generationOptions, cancellationToken);
                                if (!string.IsNullOrEmpty(summary))
                                    textContent.text = $"{documentReferencingOptions.AddedInstruction}:\r\n" + summary + "\r\n\r\n" + textContent.text;
                            }
                        }
                    }
                });
            }
            else
            {
                RemoveRequestModifier("DocumentRecall");
            }
            return this;
        }

        /// <summary>
        /// Enable and configure tool calls for document or code retrieval
        /// </summary>
        /// <param name="enableDocumentRetrieval">Set to true to enable the retrieve_documents tool</param>
        /// <param name="enableCodeRetrieval">Set to true to enable the retrieve_code tool</param>
        /// <param name="enableToolCallInstructionRequestModifier">If set to true, a request modifier will be registered which will add extra tool call instructions and a document count.</param>
        /// <returns>This llm instance</returns>
        public LLM ConfigureDocumentToolCalls(bool enableDocumentRetrieval = true, bool enableCodeRetrieval = false, bool enableToolCallInstructionRequestModifier = true)
        {
            if (!Tools.Any(a => a.Function?.Name == "retrieve_documents") && enableDocumentRetrieval)
                AddToolCall(retrieve_documents);
            else if (!enableDocumentRetrieval)
                Tools.RemoveAll(a => a.Function?.Name == "retrieve_documents");

            if (!Tools.Any(a => a.Function?.Name == "retrieve_code") && enableCodeRetrieval)
                AddToolCall(retrieve_code);
            else if (!enableCodeRetrieval)
                Tools.RemoveAll(a => a.Function?.Name == "retrieve_code");

            if (enableToolCallInstructionRequestModifier)
            {
                RegisterRequestModifier("DocumentRecall", async (messages, generationOptions, cancellationToken) =>
                {
                    if (DocumentVectorDB != null && messages.Last() is ChatUserMessage userMessage)
                    {
                        var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                        if (textContent != null)
                        {
                            if (Tools.Any(a => a.Function?.Name == "retrieve_documents"))
                            {
                                textContent.text = $"Note: There are {DocumentVectorDB.Documents.Count} documents attached to this conversation. Use the 'retrieve_documents' function to search through them."
                                    + "\r\n\r\n" + textContent.text;
                            }

                            if (Tools.Any(a => a.Function?.Name == "retrieve_code"))
                            {
                                textContent.text = $"Note: There are {DocumentVectorDB.Documents.Count} code files/documents attached to this conversation. Use the 'retrieve_documents' function to search through them."
                                    + "\r\n\r\n" + textContent.text;
                            }
                        }
                    }
                });
            }
            else
            {
                RemoveRequestModifier("DocumentToolCallInstruction");
            }
            return this;
        }

        /// <summary>
        /// Store the in-memory vector database as archive file (two files will be created: filename.data and filename.index)
        /// Saving this over an existing file will only add new documents based on document name, unless overwrite all is set to true
        /// </summary>
        /// <param name="fileName">File name of the archive to store this data in</param>
        /// <param name="overwriteAll">When set to true, a new file will be created from scratch with all documents.</param>
        /// <returns>Task for writing this data to disk</returns>
        public async Task SaveDocumentsVectorDataBaseAsync(string fileName, bool overwriteAll=false)
        {
            if (DocumentVectorDB == null)
                return;

            if (overwriteAll && File.Exists(fileName))
                File.Delete(fileName);

            var archive = new ArchiveFile(fileName);
            
            long totalTextLength = 0;
            List<string> documents = new List<string>();
            
            foreach (var document in DocumentVectorDB.Documents)
            {
                totalTextLength += document.Value.Text.Length;
                documents.Add(document.Value.SourceName);

                if (archive.Exists("document/" + document.Value.SourceName))
                    continue;

                using var stream = new MemoryStream();
                using var binaryWriter = new BinaryWriter(stream);
                binaryWriter.Write(document.Value.SourceName);
                binaryWriter.Write(document.Value.Text);

                foreach (var item in DocumentVectorDB.AsEnumerable())
                {
                    if (item.Key.Source != document.Value)
                        continue;
                    binaryWriter.Write(item.Key.Offset);
                    binaryWriter.Write(item.Key.Length);
                    binaryWriter.Write(item.Key.DocumentReferenceIndex);

                    foreach (var vector in item.Vector)
                    {
                        binaryWriter.Write(vector);
                    }
                }
                binaryWriter.Write((int)-1); // mark end

                binaryWriter.Flush();
                stream.Position = 0;

                await archive.Add("document/"+ document.Value.SourceName, stream);
            }

            var metaDataMs = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DocumentSavedMetaData()
            {
                VectorLength = DocumentVectorDB.VectorDimension,
                TotalTextLength = totalTextLength,
                Documents = documents
            })));
            metaDataMs.Position = 0;

            await archive.Add("metadata.json", metaDataMs);
        }

        /// <summary>
        /// Open an existing archive file and load all previously saved document vector data.
        /// Note that this does replace the existing in-memory vector db and all it's items. 
        /// </summary>
        /// <param name="fileName">Filename pointing to an existing archive (excluding .data / .index extension)</param>
        /// <returns>Task to read all data from disk and build the new in-memory vector db</returns>
        /// <exception cref="FileNotFoundException">Thrown when the archive file does not exists</exception>
        /// <exception cref="Exception">If the file is corrupted or otherwise incompatible, an Exception will be thrown</exception>
        public async Task LoadDocumentsVectorDataBaseAsync(string fileName)
        {
            if (!File.Exists(fileName + ".data") || !File.Exists(fileName + ".index"))
                throw new FileNotFoundException(fileName + ".data or .index not found");

            var archive = new ArchiveFile(fileName);
            if (!archive.Exists("metadata.json"))
                throw new Exception("Could not find metadata file in archive");

            if (DocumentVectorDB != null)
            {
                DocumentVectorDB.Dispose();
                DocumentVectorDB = null;
            }

            DocumentSavedMetaData? metaData;
            using (var stream = await archive.Open("metadata.json"))
            {
                if (stream == null)
                    throw new Exception("Could not open metadata file");

                metaData = JsonSerializer.Deserialize<DocumentSavedMetaData>(stream);
                if (metaData == null)
                    throw new Exception("Incompatible meta data json");

                if (metaData.VectorLength <= 0)
                    throw new Exception("Incorrect vector length in meta file");
            }

            DocumentVectorDB = new DocumentVectorDB(metaData.VectorLength);
            foreach (var document in metaData.Documents)
            {
                if (!archive.Exists("document/" + document))
                    continue; // shouldn't happen

                using var documentStream = await archive.Open("document/" + document);
                if (documentStream == null)
                    throw new Exception("Could not open stream for document " + document);

                using var binaryReader = new BinaryReader(documentStream);

                var sourceName = binaryReader.ReadString();
                var text = binaryReader.ReadString();

                var documentSource = DocumentVectorDB.UpsertDocumentSource(sourceName, text);

                while (true)
                {
                    var offset = binaryReader.ReadInt32();
                    if (offset < 0)
                        break; // End marker

                    var length = binaryReader.ReadInt32();
                    var documentReferenceIndex = binaryReader.ReadInt32();
                    var vector = new float[metaData.VectorLength];
                    for (var i = 0; i < metaData.VectorLength; i++)
                    {
                        vector[i] = binaryReader.ReadSingle();
                    }
                    DocumentVectorDB.UpsertDocumentSection(documentSource, offset, length, vector);
                }
            }
        }
        private class DocumentSavedMetaData
        {
            public int VectorLength { get; set; }
            public long TotalTextLength { get; set; }
            public List<string> Documents { get; set; } = new();
        }
        private string PromptDeepDive = "Above is a section from a document, as well as the original user instruction. Try to answer the user instruction with the text from the document. Answer in a clear short factual response. If that is not possible, answer with 'NOT FOUND'. This request will be repeated for each section of each document and all results will be combined so it's important to stay concise.";
        private string PromptDeepDiveSummarize = "Above is a combined deep-dive result you've written for each document based on the original user instruction. Turn this into a clear answer to the original user instruction.";

        /// <summary>
        /// Do a very extensive (and slow!) deep dive through the added documents.
        /// Each document will be parsed in chunks while evaluating the given instruction.
        /// All evaluations will be combined and summarized together at the end.
        /// </summary>
        /// <param name="text">Instruction or question for the LLM.</param>
        /// <param name="chunkSize">Max number of characters of the document to pass to the LLM at once.</param>
        /// <param name="overlappingSize">Number of characters to overlap between each chunk. This makes sure the LLM sees all full sentences.</param>
        /// <param name="generationOptions">Optional: Options to control the LLM generation and behaviour within this method. If not set the .DefaultGenerationOptions is used.</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel the request early</param>
        /// <returns>The textual summarized response from the LLM.</returns>
        /// <exception cref="Exception">An exception will be thrown if communication with the LLM fails.</exception>
        public async Task<string> GenerateDeepDiveDocumentsResponseAsync(string text, int chunkSize=4096, int overlappingSize = 100, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (DocumentVectorDB == null)
                return "";
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            generationOptions = generationOptions.Clone(); // Copy so we can modify the settings safely

            generationOptions.EnableRequestModifiers = false;

            // This will scan through the documents, section by section, using multiple requests
            // The LLM will try manually find all relevant information in the text and 'answer' the text using that
            // All answers will be combined and then rewritten to provide a good full deep-dive answer
            // The goal for this one is to provide an answer where the LLM have actually seen all documents instead of just a search/rank result.

            // Step 1: Go through each document
            StringBuilder sb = new StringBuilder();
            foreach (var document in DocumentVectorDB.Documents)
            {
                for (var i=0;i < document.Value.Text.Length; i += (chunkSize - overlappingSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunk = (i + chunkSize > document.Value.Text.Length) ?
                        document.Value.Text.Substring(i) :
                        document.Value.Text.Substring(i, chunkSize);

                    var prompt = GetRelatedDocumentSnippit(1, i, chunk.Length, chunk, document.Value.SourceName, document.Value.Text, null, generationOptions) + "\r\n\r\n" +
                        $"<UserInstruction>{CleanXmlValue(text)}</UserInstruction>\r\n\r\n" + PromptDeepDive;

                    var chunkResponse = await GenerateResponseAsync(prompt, generationOptions, cancellationToken);
                    if (chunkResponse == null)
                        throw new Exception("Could not get LLM response");

                    var str = chunkResponse.LastAsString() ?? "";
                    if (str.Contains("NOT FOUND"))
                        continue;

                    sb.AppendLine(str);
                }
            }

            // Step 2: Create a single response
            var promptSummarize = $"<DeepDiveResult>{CleanXmlValue(sb.ToString())}</DeepDiveResult>\r\n\r\n" + 
                $"<UserInstruction>{CleanXmlValue(text)}</UserInstruction>\r\n\r\n" + PromptDeepDiveSummarize;

            var summarizeResponse = await GenerateResponseAsync(promptSummarize, generationOptions, cancellationToken);
            if (summarizeResponse == null)
                throw new Exception("Could not get LLM response");

            return summarizeResponse.LastAsString() ?? "";  
        }
        private static string CleanXmlValue(string text)
        {
            return text.Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>
        /// Generate a summary of related documents from the vector database based on the given text.
        /// If there is more than one matching result, the most relevant ones will be combined until the maxSize is reached.
        /// </summary>
        /// <param name="text">Text to find related documents for</param>
        /// <param name="documentReferencingOptions">Options to control the format and length of the text generated to reference the documents.</param>
        /// <param name="generationOptions">Optional: Options to control the LLM generation and behaviour within this method. If not set the .DefaultGenerationOptions is used.</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel the request early</param>
        /// <returns>A formatted string with the found documents. This includes their document name, offset within the document and text from the document.</returns>
        public async Task<string> GenerateRelatedDocumentsSummaryAsync(string text, DocumentReferencingOptions? documentReferencingOptions = null, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (documentReferencingOptions == null)
                documentReferencingOptions = new DocumentReferencingOptions(); // defaults

            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (documentReferencingOptions.OnDocumentRelatedText != null)
                text = documentReferencingOptions.OnDocumentRelatedText(text);

            if (DocumentVectorDB == null || string.IsNullOrWhiteSpace(text))
                return ""; // No document added means no summary

            // TODO: If we found our given text exactly within one of the documents, we want to prefer that

            // Get the most matching documents using embeddings and then rank them. 
            var embedding = await GenerateEmbeddingsAsync(text, generationOptions, cancellationToken);
            var results = DocumentVectorDB.Search(embedding, 50);
            results = await GenerateRankingsAsync(text, results.Select(r => r.Item).ToList(), top_n: 10, documentReferencingOptions.RelevanceThreshold, generationOptions, cancellationToken);

            // Combine multiple found results within the same file if they are close to each other.            
            var combinedResults = DocumentVectorDB.CombineCloseResults(results, documentReferencingOptions.CombineCloseCharacterCount);

            if (combinedResults.Any(a=>a.Item.Length > documentReferencingOptions.MaxSize)) // When combining results including nearby results, we might exceed max size, so revert to just overlapping results
                combinedResults = DocumentVectorDB.CombineCloseResults(results, 0);

            if (combinedResults.Any(a => a.Item.Length > documentReferencingOptions.MaxSize)) // When combining just the overlapping results, we still might exceed max size, so revert to non-combined results
                combinedResults = results;

            results = combinedResults.OrderByDescending(a => a.Score).ToList();

            if (results.Count == 0)
                return ""; // No results

            // Finally, combine them in a text string which can be used within a prompt, continue until max size is reached.

            var documents = new List<object>();

            var sb = new StringBuilder();
            if (documentReferencingOptions.Format == DocumentReferencingFormat.XML)
            {
                sb.AppendLine($"<{documentReferencingOptions.XMLDocumentsTagName}>");
            }
            else if (documentReferencingOptions.Format == DocumentReferencingFormat.Json)
            {
//                sb.AppendLine("```json");
                sb.AppendLine("[");
            }

            var index = 0;
            foreach (var result in results)
            {                
                var relatedText = result.Item.GetTextSection();

                var start = result.Item.Offset;
                var length = result.Item.Length;

                index++;
                var textToAddShort = GetRelatedDocumentSnippit(index, start, length, relatedText.ToString(), result.Item.Source.SourceName, result.Item.Source.Text, documentReferencingOptions, generationOptions);

                if (documentReferencingOptions.ExpandBeforeCharacterCount > 0 || documentReferencingOptions.ExpandAfterCharacterCount > 0)
                {
                    // Expand the text a bit to provide more context
                    start = Math.Max(0, result.Item.Offset - documentReferencingOptions.ExpandBeforeCharacterCount);
                    var end = Math.Min(result.Item.Source.Text.Length, result.Item.Offset + result.Item.Length + documentReferencingOptions.ExpandAfterCharacterCount);
                    length = end - start;

                    // TODO: Nudge the start/end to the nearest space or even nearest sentence endings.
                    relatedText = result.Item.Source.Text.AsMemory(start, length);
                }

                var textToAdd = GetRelatedDocumentSnippit(index, start, length, relatedText.ToString(), result.Item.Source.SourceName, result.Item.Source.Text, documentReferencingOptions, generationOptions);

                if (sb.Length + textToAdd.Length + documentReferencingOptions.XMLDocumentsTagName.Length + 5 > documentReferencingOptions.MaxSize) // Exceeding max size with the long text, try adding the short version
                {
                    if (sb.Length + textToAddShort.Length + documentReferencingOptions.XMLDocumentsTagName.Length + 5 <= documentReferencingOptions.MaxSize) // Fits with the short version
                    {
                        if (documentReferencingOptions.Format == DocumentReferencingFormat.Json && index > 1)
                            sb.Append(",");
                        sb.AppendLine(textToAddShort);
                        sb.AppendLine();
                    }
                    break;
                }

                if (documentReferencingOptions.Format == DocumentReferencingFormat.Json && index > 1)
                    sb.Append(",");
                sb.AppendLine(textToAdd);
                sb.AppendLine();
            }

            if (documentReferencingOptions.Format == DocumentReferencingFormat.XML)
            {
                sb.AppendLine($"</{documentReferencingOptions.XMLDocumentsTagName}>");
            }
            else if (documentReferencingOptions.Format == DocumentReferencingFormat.Json)
            {
                sb.AppendLine("]");
                //sb.AppendLine("```");
            }

            return sb.ToString();
        }
        private string GetRelatedDocumentSnippit(int index, long offset, long length, string relatedText, string sourceName, string sourceText, DocumentReferencingOptions? documentReferencingOptions = null, LLMGenerationOptions? generationOptions = null)
        {
            if (documentReferencingOptions == null)
                documentReferencingOptions = new DocumentReferencingOptions(); // defaults
            if (offset > 0)
                relatedText = "[...] " + relatedText;

            if (offset + length < sourceText.Length)
                relatedText += " [...]";

            var format = documentReferencingOptions.Format;

            if (format == DocumentReferencingFormat.XML)
            {
                return $"  <{documentReferencingOptions.XMLDocumentTagName} index=\"{index}\">\r\n" +
                    $"    <source>{CleanXmlValue(sourceName)}</source>\r\n" +
                    $"    <offset>{offset}</offset>\r\n" +
                    $"    <{documentReferencingOptions.XMLDocumentTagName}_content>" +
                    CleanXmlValue(relatedText.ToString()) +
                    $"</{documentReferencingOptions?.XMLDocumentTagName}_content>\r\n" +
                    $"  </{documentReferencingOptions?.XMLDocumentTagName}>";
            }
            else if (format == DocumentReferencingFormat.Markdown)
            {
                return $"### Context from \"{sourceName}\" (Offset: {offset})\r\n{relatedText}\r\n\r\n";
            }

            // JSON (fallback/default)
            return JsonSerializer.Serialize(new RelatedDocumentItem()
            {
                id = index,
                source = sourceName,
                offset = offset,
                content = relatedText
            }, new JsonSerializerOptions()
            {
                WriteIndented = true
            });
        }

        [ToolCall("Search in the attached documents based on the given text (uses embedding, vector db search)")]
        private async Task<RetrieveDocumentsResult> retrieve_documents([ToolCall("The search query to find relevant passages. It should be a semantic sentence, not just keywords", Required = true)]string query)
        {
            Console.WriteLine("Retrieve documents function called with text: " + query);
            if (DocumentVectorDB == null)
                return new RetrieveDocumentsResult();

            var json = await GenerateRelatedDocumentsSummaryAsync(query, new DocumentReferencingOptions()
            {
                Format = DocumentReferencingFormat.Json 
            }); // TODO: Custom documentreferenceoptions
            if (string.IsNullOrEmpty(json))
                return new RetrieveDocumentsResult();
            var items = JsonSerializer.Deserialize<List<RelatedDocumentItem>>(json);


            return new RetrieveDocumentsResult()
            {
                results = items ?? new()
            };
            
        }

        [ToolCall("Search in the attached code files based on the given text (uses embedding, vector db search)")]
        private async Task<RetrieveDocumentsResult> retrieve_code([ToolCall("A piece of code like a method declaration found within the code", Required = true)] string query)
        {
            // TODO: Code specific syntax tricks to more easily return just a single method body etc.
            return await retrieve_documents(query);
        }
        public class RetrieveDocumentsResult
        {
            public List<RelatedDocumentItem> results { get; set; } = new();
        }
        public class RelatedDocumentItem
        {
            public int id { get; set; }
            public string source { get; set; }
            public long offset { get; set; }
            public string content { get; set; }
            
        }

        public enum DocumentEmbedMode
        {
            AsIs = 0,
            SplitSentence = 1,
            Overlapping = 2,
            DontEmbed = 3
        }

        public enum DocumentReferencingFormat
        {
            None = 0,
            Json = 1,
            XML = 2,
            Markdown = 3
        }
    }
}
