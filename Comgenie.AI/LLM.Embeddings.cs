using Comgenie.AI.Entities;
using Comgenie.Util;
using System;
using System.Collections;
using System.Collections.Generic;
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
        

        public int DocumentChunkCharacterCount { get; set; } = 200;
        public int DocumentOverlappingCharacterCount { get; set; } = 100;

        /// <summary>
        /// Add a document to the vector database by generating embeddings for it.
        /// The strategy can be controlled using the mode parameter.
        /// </summary>
        /// <param name="documentName">Name of the document to add. If a document with this name was already added before, all document and vector data will be overriden.</param>
        /// <param name="documentText">Full text representation of the document</param>
        /// <param name="mode">Strategy to create searchable embeddings for the vector database</param>
        /// <returns>Task as it will have to communicate with the LLM to generate embeddings.</returns>
        public async Task AddDocument(string documentName, string documentText, DocumentEmbedMode mode = DocumentEmbedMode.Overlapping)
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
                    var nextPeriod = documentText.IndexOf('.', index);
                    if (nextPeriod == -1)
                    {
                        nextPeriod = documentText.Length;
                    }
                    else
                    {
                        nextPeriod++; // Include the period in the current sentence
                    }

                    await GenerateEmbeddingsAndAddDocumentSection(document, index, nextPeriod - index);
                    index = nextPeriod;
                }
            }
            else if (mode == DocumentEmbedMode.Overlapping)
            {
                int chunkSize = DocumentChunkCharacterCount;
                int overlapSize = DocumentOverlappingCharacterCount;
                for (var i=0;i<documentText.Length; i+= overlapSize)
                {
                    var length = Math.Min(chunkSize, documentText.Length - i);
                    await GenerateEmbeddingsAndAddDocumentSection(document, i, length);
                }
            }
            else if (mode == DocumentEmbedMode.DontEmbed)
            {
                return; // Don't embed in prompt. Used for actions like deep dive.
            }
            else
            {
                await GenerateEmbeddingsAndAddDocumentSection(document, 0, documentText.Length);
            }
        }
        private async Task GenerateEmbeddingsAndAddDocumentSection(DocumentVectorDB.DocumentSource document, int offset, int length)
        {
            if (DocumentVectorDB == null)
                throw new Exception("DocumentVectorDB is null");

            var embedding = await GenerateEmbeddingsAsync(document.Text.Substring(offset, length));
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
        /// Store the in-memory vector database as archive file (two files will be created: filename.data and filename.index)
        /// Saving this over an existing file will only add new documents based on document name, unless overwrite all is set to true
        /// </summary>
        /// <param name="fileName">File name of the archive to store this data in</param>
        /// <param name="overwriteAll">When set to true, a new file will be created from scratch with all documents.</param>
        /// <returns>Task for writing this data to disk</returns>
        public async Task SaveDocumentsVectorDataBase(string fileName, bool overwriteAll=false)
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
        public async Task LoadDocumentsVectorDataBase(string fileName)
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
        /// <returns>The textual summarized response from the LLM.</returns>
        /// <exception cref="Exception">An exception will be thrown if communication with the LLM fails.</exception>
        public async Task<string> GenerateDeepDiveDocumentsResponseAsync(string text, int chunkSize=4096, int overlappingSize = 100, LLMGenerationOptions? generationOptions = null, CancellationToken? cancellationToken = null)
        {
            if (DocumentVectorDB == null)
                return "";
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            generationOptions = generationOptions.Clone(); // Copy so we can modify the settings safely

            generationOptions.DocumentReferencingMode = DocumentReferencingMode.None;

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
                    if (cancellationToken.HasValue)
                        cancellationToken.Value.ThrowIfCancellationRequested();

                    var chunk = (i + chunkSize > document.Value.Text.Length) ?
                        document.Value.Text.Substring(i) :
                        document.Value.Text.Substring(i, chunkSize);

                    var prompt = GetRelatedDocumentSnippit(1, i, chunk.Length, chunk, document.Value.SourceName, document.Value.Text, generationOptions.DocumentReferencingMode) + "\r\n\r\n" +
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
        /// <param name="generationOptions">Options to control the format and length of the text generated to reference the documents.</param>
        /// <returns>A formatted string with the found documents. This includes their document name, offset within the document and text from the document.</returns>
        public async Task<string> GenerateRelatedDocumentsSummaryAsync(string text, LLMGenerationOptions? generationOptions = null, CancellationToken? cancellationToken = null)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (DocumentVectorDB == null)
                return ""; // No document added means no summary

            // TODO: If we found our given text exactly within one of the documents, we want to prefer that

            // Get the most matching documents using embeddings and then rank them. 
            var embedding = await GenerateEmbeddingsAsync(text, generationOptions, cancellationToken);
            var results = DocumentVectorDB.Search(embedding, 50);
            results = await GenerateRankingsAsync(text, results.Select(r => r.Item).ToList(), top_n: 10, generationOptions, cancellationToken);

            // Combine multiple found results within the same file if they are close to each other.            
            var combinedResults = DocumentVectorDB.CombineCloseResults(results, generationOptions.DocumentReferencingCombineCloseCharacterCount);

            if (combinedResults.Any(a=>a.Item.Length > generationOptions.DocumentReferencingMaxSize)) // When combining results including nearby results, we might exceed max size, so revert to just overlapping results
                combinedResults = DocumentVectorDB.CombineCloseResults(results, 0);

            if (combinedResults.Any(a => a.Item.Length > generationOptions.DocumentReferencingMaxSize)) // When combining just the overlapping results, we still might exceed max size, so revert to non-combined results
                combinedResults = results;

            results = combinedResults.OrderByDescending(a => a.Score).ToList();

            if (results.Count == 0)
                return ""; // No results

            // Finally, combine them in a text string which can be used within a prompt, continue until max size is reached.

            var documents = new List<object>();

            var sb = new StringBuilder();
            if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.XML)
            {
                sb.AppendLine("<documents>");
            }
            else if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.Json)
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
                var textToAddShort = GetRelatedDocumentSnippit(index, start, length, relatedText.ToString(), result.Item.Source.SourceName, result.Item.Source.Text, generationOptions.DocumentReferencingMode);

                if (generationOptions.DocumentReferencingExpandBeforeCharacterCount > 0 || generationOptions.DocumentReferencingExpandAfterCharacterCount > 0)
                {
                    // Expand the text a bit to provide more context
                    start = Math.Max(0, result.Item.Offset - generationOptions.DocumentReferencingExpandBeforeCharacterCount);
                    var end = Math.Min(result.Item.Source.Text.Length, result.Item.Offset + result.Item.Length + generationOptions.DocumentReferencingExpandAfterCharacterCount);
                    length = end - start;

                    // TODO: Nudge the start/end to the nearest space or even nearest sentence endings.
                    relatedText = result.Item.Source.Text.AsMemory(start, length);
                }

                var textToAdd = GetRelatedDocumentSnippit(index, start, length, relatedText.ToString(), result.Item.Source.SourceName, result.Item.Source.Text, generationOptions.DocumentReferencingMode);

                if (sb.Length + textToAdd.Length + 21 > generationOptions.DocumentReferencingMaxSize) // Exceeding max size with the long text, try adding the short version
                {
                    if (sb.Length + textToAddShort.Length + 21 <= generationOptions.DocumentReferencingMaxSize) // Fits with the short version
                    {
                        if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.Json && index > 1)
                            sb.Append(",");
                        sb.AppendLine(textToAddShort);
                        sb.AppendLine();
                    }
                    break;
                }

                if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.Json && index > 1)
                    sb.Append(",");
                sb.AppendLine(textToAdd);
                sb.AppendLine();
            }

            if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.XML)
            {
                sb.AppendLine("</documents>");
            }
            else if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.Json)
            {
                sb.AppendLine("]");
                //sb.AppendLine("```");
            }

            return sb.ToString();
        }
        private static string GetRelatedDocumentSnippit(int index, long offset, long length, string relatedText, string sourceName, string sourceText, DocumentReferencingMode format)
        {
            if (offset > 0)
                relatedText = "[...] " + relatedText;

            if (offset + length < sourceText.Length)
                relatedText += " [...]";

            if (format == DocumentReferencingMode.XML)
            {
                return $"  <document index=\"{index}\">\r\n" +
                    $"    <source>{CleanXmlValue(sourceName)}</source>\r\n" +
                    $"    <offset>{offset}</offset>\r\n" +
                    $"    <document_content>" + CleanXmlValue(relatedText.ToString()) + "</document_content>" +
                    "  </document>";
            }
            else if (format == DocumentReferencingMode.Markdown)
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

        /// <summary>
        /// Do a call to the embeddings endpoint to generate embeddings for the given text.
        /// Note that the number of floats returned depends on the model used. 
        /// When using llama-server, make sure the --embeddings and --pooling arguments are set, recommended arguments: --embeddings --pooling mean 
        /// </summary>
        /// <param name="text">Text to get embeddings from</param>
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
            }, generationOptions.FailedRequestRetryCount, cancellationToken);
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
        /// <returns>List of scored items with their relevance score</returns>
        public async Task<List<ScoredItem<T>>> GenerateRankingsAsync<T>(string text, List<T> items, int top_n=10, LLMGenerationOptions? generationOptions = null, CancellationToken? cancellationToken = null)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

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
                return results.OrderByDescending(a => a.Score).ToList();
            }, generationOptions.FailedRequestRetryCount, cancellationToken);
            
            return results;
        }

        // Extra helper to work with simple string documents directly
        public Task<List<ScoredItem<string>>> GenerateRankingsAsync(string text, List<string> documents, int top_n = 10, LLMGenerationOptions? generationOptions = null, CancellationToken? cancellationToken = null)
        {
            return GenerateRankingsAsync<string>(text, documents, top_n, generationOptions, cancellationToken);
        }


        [ToolCall("Search in the attached documents based on the given text (uses embedding, vector db search)")]
        private async Task<RetrieveDocumentsResult> retrieve_documents([ToolCall("The search query to find relevant passages. It should be a semantic sentence, not just keywords", Required = true)]string query)
        {
            Console.WriteLine("Retrieve documents function called with text: " + query);
            if (DocumentVectorDB == null)
                return new RetrieveDocumentsResult();

            var generationOptions = DefaultGenerationOptions.Clone();
            generationOptions.DocumentReferencingMode = DocumentReferencingMode.Json;

            var json = await GenerateRelatedDocumentsSummaryAsync(query, generationOptions);
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

            Console.WriteLine("Retrieve code files called with text: " + query);
            if (DocumentVectorDB == null)
                return new RetrieveDocumentsResult();

            var json = await GenerateRelatedDocumentsSummaryAsync(query, DefaultGenerationOptions);
            var items = JsonSerializer.Deserialize<List<RelatedDocumentItem>>(json);

            return new RetrieveDocumentsResult()
            {
                results = items ?? new()
            };
        }
        private class RetrieveDocumentsResult
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

        public enum DocumentReferencingMode
        {
            None = 0,
            FunctionCallDocuments = 1,
            FunctionCallCode = 2,
            Json = 3,
            XML = 4,
            Markdown = 5
        }
    }
}
