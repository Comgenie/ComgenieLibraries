using Comgenie.AI.Entities;
using Comgenie.AI.Memory;
using Comgenie.AI.Memory.Entities;
using System.Text;
using System.Text.Json;
using static Comgenie.AI.LLM;

namespace Comgenie.AI
{
    public static class LLMMemoryExtensions
    {
        public static Dictionary<LLM, Memory.Memory> MemoryInstances { get; set; } = new();
        private static async Task<Memory.Memory> GetMemoryByLLM(LLM llm, bool initializeVectorDb = true)
        {
            if (!MemoryInstances.ContainsKey(llm))
                MemoryInstances[llm] = new Memory.Memory();

            if (initializeVectorDb && MemoryInstances[llm].MemoryDB == null)
            {
                await MemoryInstances[llm].InitializeMemoryDbAsync(llm);
                if (MemoryInstances[llm].MemoryDB == null)
                    throw new Exception("Could not initialize vector db");
            }

            return MemoryInstances[llm];
        }
        public static async Task SaveMemoryAsync(this LLM llm, Stream stream)
        {
            var memory = await GetMemoryByLLM(llm);
            if (memory == null)
                return;

            using var writer = new BinaryWriter(stream);
            writer.Write((Int32)memory.Entries.Count);
            // TODO: Store 'dummy embeddings' to check for size differences
            foreach (var entry in memory.Entries)
            {
                var json = JsonSerializer.Serialize(entry);
                writer.Write(json);
                writer.Write((Int32)(entry.Embeddings?.Length ?? 0));
                if (entry.Embeddings != null && entry.Embeddings.Length > 0)
                {
                    for (var i=0;i<entry.Embeddings.Length;i++)
                    {
                        writer.Write((float)entry.Embeddings[i]);
                    }
                }
            }

            writer.Write((Int32)memory.NamedEntities.Count);
            foreach (var namedEntity in memory.NamedEntities)
            {
                var json = JsonSerializer.Serialize(namedEntity);
                writer.Write(json);
                writer.Write((Int32)0);
                // Reserved for embeddings
            }
        }
        public static async Task SaveMemoryAsync(this LLM llm, string filePath)
        {
            using var file = File.Create(filePath);
            await SaveMemoryAsync(llm, file);
        }
        public static async Task LoadMemoryAsync(this LLM llm, Stream stream)
        {
            var memory = new AI.Memory.Memory();
            if (memory == null)
                throw new Exception("Could not load memory from stream");

            using var reader = new BinaryReader(stream);
            var entryCount = reader.ReadInt32();
            for (var i= 0;i< entryCount; i++) {
                var json = reader.ReadString();
                var entry = JsonSerializer.Deserialize<MemoryEntry>(json);
                if (entry == null)
                    throw new Exception("Could not deserialize entry " + json);
                var embeddingsCount = reader.ReadInt32();
                if (embeddingsCount > 0)
                {
                    entry.Embeddings = new float[embeddingsCount];
                    for (var j=0;j<embeddingsCount;j++)
                    {
                        entry.Embeddings[j] = reader.ReadSingle();
                    }
                }
                memory.Entries.Add(entry);
            }

            var namedEntityCount = reader.ReadInt32();
            for (var i = 0; i < entryCount; i++)
            {
                var json = reader.ReadString();
                var namedEntity = JsonSerializer.Deserialize<MemoryNamedEntity>(json);
                if (namedEntity == null)
                    throw new Exception("Could not deserialize named entity " + json);

                var embeddingsCount = reader.ReadInt32();
                // Reserved for embeddings

                memory.NamedEntities.Add(namedEntity);
            }

            MemoryInstances[llm] = memory;
        }
        public static async Task LoadMemoryAsync(this LLM llm, string filePath)
        {
            using var file = File.OpenRead(filePath);
            await LoadMemoryAsync(llm, file);
        }

        public static async Task AddTextToMemoryAsync(this LLM llm, string text, string mode = "text", string source = "Text", LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            var memory = await GetMemoryByLLM(llm);

            await TextAnalyzer.ProcessTextAsync(llm, text, mode, source, memory, DateTime.Now, null, generationOptions, cancellationToken);
        }
        public static async Task AddSessionToMemoryAsync(this LLM llm, List<ChatMessage> messages, string assistantName, string userName, string mode = "conversation", string source = "Conversation", LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            var transcript = TextAnalyzer.ExtractTranscript(messages, assistantName, userName);
            await AddTextToMemoryAsync(llm, transcript, mode, source, generationOptions, cancellationToken);
        }
        
        public static async Task<string> GenerateRelatedMemorySummaryAsync(this LLM llm, string text, int maxLengthMemories = 500, int maxLengthEntities = 1000, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            var memory = await GetMemoryByLLM(llm);

            generationOptions = (generationOptions ?? llm.DefaultGenerationOptions).Clone(options =>
            {
                options.EnableRequestModifiers = false; // Prevent infinite loops
            });
            
            List<MemoryNamedEntity> EntitiesMentioned = new();
            if (maxLengthEntities > 0) // Get all the names being used in the text
            {
                var namedEntities = await llm.GenerateStructuredResponseAsync<List<string>>(new List<ChatMessage>()
                {
                    new ChatSystemMessage("You are an assistent helping to extract information from sentences"),
                    new ChatUserMessage("Please find all the named entities (People, Animals, Locations, Websites, etc.) from the following sentence:\r\n" + text)
                }, true, generationOptions, cancellationToken);

                if (namedEntities != null)
                {
                    foreach (var namedEntity in namedEntities)
                    {
                        //if (namedEntity.Equals(Memory.AssistantName, StringComparison.OrdinalIgnoreCase))
                        //    continue; // Ignore or else the assistant will keep reinforcing the same stories

                        var foundEntities = memory.NamedEntities.Where(a =>
                            a.Analysis != null &&
                            (a.Name != null && a.Name.Equals(namedEntity, StringComparison.OrdinalIgnoreCase)) ||
                            a.Aliases.Contains(namedEntity, StringComparer.OrdinalIgnoreCase)).ToList();
                        EntitiesMentioned.AddRange(foundEntities.Where(a => !EntitiesMentioned.Contains(a)).Take(3));
                    }
                }
            }

            var embedding = await llm.GenerateEmbeddingsAsync(text, generationOptions, cancellationToken);
            var results = memory.MemoryDB!.Search(embedding, 50);
            results = await llm.GenerateRankingsAsync(text, results.Select(r => r.Item).ToList(), top_n: 10, 0.9, generationOptions, cancellationToken);

            foreach (var item in results)
            {
                item.Score = item.Score * (float)item.Item.DecayFactor;
            }

            // Increase score for memory items where we had the same emotional state (TODO)
            /*foreach (var item in results)
            {
                if (item.Item.Emotion == null)
                    continue;

                var similarity = item.Item.Emotion.Similarity(CurrentEmotionalState);
                if (similarity > 0.5)
                    item.Score += (similarity * 0.1f);
            }*/

            results = results.Where(a => a.Score > 0.8).OrderByDescending(r => r.Score).ToList();

            if (results.Count == 0)
                return "";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<memories>");
            foreach (var result in results)
            {
                sb.AppendLine("  <memory>" + result.Item.ToString().Replace("<", "&lt;").Replace(">", "&gt;") + "</memory>");
                result.Item.RecallCount++;
                result.Item.LastRecalled = DateTime.UtcNow;
                if (sb.Length > maxLengthMemories)
                    break;
            }
            maxLengthEntities = sb.Length + maxLengthEntities;
            foreach (var entityMentioned in EntitiesMentioned)
            {
                sb.AppendLine("  <memory>" + entityMentioned?.Analysis?.Summary + "</memory>");
                if (sb.Length > maxLengthEntities)
                    break;
            }
            sb.AppendLine("</memories>");

            return sb.ToString();
        }

        public static LLM ConfigureMemoryRecallRequestModifier(this LLM llm, bool enabled = true, int maxLengthMemories = 500, int maxLengthEntities = 1000)
        {
            if (enabled)
            {
                llm.RegisterRequestModifier("MemoryRecall", async (messages, generationOptions, cancellationToken) =>
                {
                    if (messages.Last() is ChatUserMessage userMessage)
                    {
                        var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                        if (textContent != null)
                        {
                            var summary = await GenerateRelatedMemorySummaryAsync(llm, textContent.text, maxLengthMemories,maxLengthEntities, generationOptions, cancellationToken);
                            if (!string.IsNullOrEmpty(summary))
                                textContent.text = summary + "\r\n\r\n" + textContent.text;
                        }
                    }
                });
            }
            else
            {
                llm.RemoveRequestModifier("MemoryRecall");
            }

            return llm;
        }

        public static LLM ConfigureMemoryToolCalls(this LLM llm, bool enableRetrieval = true, bool enableStoring = false)
        {
            var toolInstance = new ToolInstance(llm);

            if (!llm.Tools.Any(a=>a.Function?.Name == "search_memory") && enableRetrieval)
                llm.AddToolCall(toolInstance.search_memory);
            else if (!enableRetrieval)
                llm.Tools.RemoveAll(a => a.Function?.Name == "search_memory");

            if (!llm.Tools.Any(a => a.Function?.Name == "add_memory") && enableStoring)
                llm.AddToolCall(toolInstance.add_memory);
            else if (!enableStoring)
                llm.Tools.RemoveAll(a => a.Function?.Name == "add_memory");

            return llm;
        }

        private class ToolInstance(LLM llm)
        {
            
            [ToolCall("Search in the saved memories based on the given text")]
            public async Task<string> search_memory([ToolCall("The search query to find relevant passages. It should be a semantic sentence, not just keywords", Required = true)] string query)
            {
                Console.WriteLine("[Search Memory] " + query);
                
                var memories = await llm.GenerateRelatedMemorySummaryAsync(query);
                if (string.IsNullOrEmpty(memories))
                    return "No memories found matching this query";

                Console.WriteLine("[Found] " + memories);
                return memories;
            }

            [ToolCall("Add a text containing facts to store. These facts can be recalled at a later time.")]
            public async Task<string> add_memory([ToolCall("A text to store within your memory, recommended to always reference to people/entities in 3rd person form. All important related information should be provided.", Required = true)] string text)
            {
                Console.WriteLine("[Add to Memory] " + text);

                await llm.AddTextToMemoryAsync(text);
                
                return "Text added to memory";
            }
        }
    }
}
