using Comgenie.AI;
using Comgenie.AI.Entities;
using Comgenie.AI.Memory.Entities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace Comgenie.AI.Memory
{
    public class Memory
    {
        public List<MemoryEntry> Entries { get; set; } = new();
        public List<MemoryNamedEntity> NamedEntities { get; set; } = new();

        [JsonIgnore]
        public VectorDB<MemoryEntry>? MemoryDB { get; set; }

        public async Task InitializeMemoryDbAsync(LLM llm)
        {
            if (MemoryDB != null)
                return;

            var dummyEmbeddings = await llm.GenerateEmbeddingsAsync("Dummy text");
            MemoryDB = new(dummyEmbeddings.Length);
            
            foreach (var entry in Entries)
            {
                await UpsertMemoryAsync(llm, entry, false);
            }
        }

        public async Task UpsertMemoryAsync(LLM llm, MemoryEntry memoryEntry, bool refreshEmbeddings = true)
        {
            await InitializeMemoryDbAsync(llm);
            if (refreshEmbeddings || memoryEntry.Embeddings == null)
                memoryEntry.Embeddings = await GenerateMemoryEmbeddingsAsync(llm, memoryEntry);

            MemoryDB!.Upsert(memoryEntry, memoryEntry.Embeddings);
            if (!Entries.Contains(memoryEntry))
                Entries.Add(memoryEntry);
        }


        public void Save(string file)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
            };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(file, json);
        }
        public static Memory Load(string file)
        {
            if (!File.Exists(file))
                return new Memory();

            var json = File.ReadAllText(file);
            var memory = JsonSerializer.Deserialize<Memory>(json);
            return memory ?? new Memory();
        }

        private static bool IsConflicting(string? source, string? target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return false;

            return !string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        }

        public MemoryNamedEntity? FindSimilarNamedEntity(MemoryNamedEntity otherEntity)
        {
            var entityQuery = this.NamedEntities.AsQueryable();

            if (!string.IsNullOrEmpty(otherEntity.Name))
                entityQuery = entityQuery.Where(a => a.Name != null && a.Name.Equals(otherEntity.Name, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(otherEntity.Type))
                entityQuery = entityQuery.Where(a => a.Type != null && a.Type.Equals(otherEntity.Type, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(otherEntity.LocationCity))
                entityQuery = entityQuery.Where(a => a.LocationCity == null || a.LocationCity.Equals(otherEntity.LocationCity, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(otherEntity.LocationCountry)) 
                entityQuery = entityQuery.Where(a => a.LocationCountry == null || a.LocationCountry.Equals(otherEntity.LocationCountry, StringComparison.OrdinalIgnoreCase));

            // Early check (name + type)
            if (!string.IsNullOrEmpty(otherEntity.Name))
            {
                var found = entityQuery.Take(2).ToList();

                if (found.Count == 1)
                    return found.First(); // Exact match
            }

            // Deeper search, find using aliases. We will give a score based on how much it matches the other entity
            // TODO: Smarter similarity check, in case of typos etc?
            Dictionary<MemoryNamedEntity, int> possibleEntities = new Dictionary<MemoryNamedEntity, int>();
            foreach (var existingEntity in this.NamedEntities)
            {
                var score = 0;

                // Hard distinct properties
                if (IsConflicting(existingEntity.Type, otherEntity.Type))
                    continue;
                if (IsConflicting(existingEntity.LocationCountry, otherEntity.LocationCountry))
                    continue;
                if (IsConflicting(existingEntity.LocationCity, otherEntity.LocationCity))
                    continue;

                if (!string.IsNullOrEmpty(existingEntity.Name) && existingEntity.Name.Equals(otherEntity.Name, StringComparison.OrdinalIgnoreCase))
                    score += 2; // Exact same name. usually it's already been catched by the early check above but if multiple were found we end up here

                // Our name is within the aliases of the existing item
                if (!string.IsNullOrEmpty(otherEntity.Name) && existingEntity.Aliases.Contains(otherEntity.Name, StringComparer.OrdinalIgnoreCase))
                    score += 1;

                foreach (var otherAlias in otherEntity.Aliases)
                {
                    // One of the aliases is the same
                    if (!string.IsNullOrEmpty(otherAlias) && existingEntity.Aliases.Contains(otherAlias, StringComparer.OrdinalIgnoreCase))
                        score++;

                    // One of the aliases is actually the name of the existing item
                    else if (!string.IsNullOrEmpty(otherAlias) && !string.IsNullOrEmpty(existingEntity.Name) && existingEntity.Name.Equals(otherAlias, StringComparison.OrdinalIgnoreCase))
                        score++;
                }

                if (score > 0)
                    possibleEntities.Add(existingEntity, score);
            }

            if (possibleEntities.Count > 0) // TODO: With a low score we might want to ask the LLM to really confirm it
                return possibleEntities.OrderByDescending(a => a.Value).FirstOrDefault().Key;

            // Not found
            return null;
        }

        public async Task<MemoryEntry?> FindSimilarMemoryAsync(LLM llm, MemoryEntry other)
        {
            await InitializeMemoryDbAsync(llm);

            var otherEmbeddings = await GenerateMemoryEmbeddingsAsync(llm, other);
            var similarMemories = MemoryDB!.Search(otherEmbeddings, 50);
            if (similarMemories.Count == 0)
                return null;

            var generationOptions = llm.DefaultGenerationOptions.Clone();

            similarMemories = await llm.GenerateRankingsAsync(other.ToString(), similarMemories.Select(r => r.Item).ToList(), top_n: 5, 0.9, generationOptions);
            foreach (var similarMemory in similarMemories)
            {
                var memoryEntry = similarMemory.Item;
                if (memoryEntry != null)
                {
                    // Check to see if they both got a specific mentioned time period.
                    // If they are not near each other we won't see them as similar
                    if (memoryEntry.LatestMentionedDate.HasValue && other.LatestMentionedDate.HasValue &&
                        memoryEntry.EarliestMentionedDate.HasValue && other.EarliestMentionedDate.HasValue
                        && (other.EarliestMentionedDate.Value > memoryEntry.LatestMentionedDate.Value.AddDays(7) ||
                            other.LatestMentionedDate.Value < memoryEntry.EarliestMentionedDate.Value.AddDays(-7)))
                        continue; // more than 7 days apart

                    if (memoryEntry.MemoryMergeGroup != other.MemoryMergeGroup)
                        continue; // Only group the same together

                    return memoryEntry;
                }
            }

            return null;
        }
        private async Task<float[]> GenerateMemoryEmbeddingsAsync(LLM llm, MemoryEntry memoryEntry)
        {
            return await llm.GenerateEmbeddingsAsync(memoryEntry.ToString());
        }
    }
    
    public class MemoryEntry
    {
        public override string ToString() // Required for Reranking llm results
        {
            // Note, this is the contents which will also be used to calculate embeddings and will be injected in full into the memories tag when recalled.
            // TODO: See if we want to include the source or other info as well
            return (MemoryMergeGroup != null ? "(" + MemoryMergeGroup + ") " : "") + Content;
        }
        public double DecayFactor
        {
            get
            {
                // TODO: If a memory entry hasn't been merged/recalled often and recent, reduce this factor so it 'decays' 
                return 1.0;
            }
        }

        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        
        public string? Source { get; set; } // Conversation, Note2Self, etc.
        public required string Content { get; set; }
        public double ImportanceScore { get; set; } = 0.5; // From 0.0 (not important, will be 'forgotten' after a while without recall) to 1.0 (very important, will never be forgotten)
        public bool Forgotten { get; set; }
        public double CertaintyScore { get; set; } = 1.0; // Higher = more certain that it's true. Goes higher with each merge, lower when contradicting
        public int MergedCount { get; set; } // Increased every time this memory is merged, reinforcing it 
        public string? MemoryMergeGroup { get; set; } // Limit merging only for items with the same group
        public List<string> AssociatedNamedEntities { get; set; } = new(); // Associated entities with a name

        public DateTime? EarliestMentionedDate { get; set; }
        public DateTime? LatestMentionedDate { get; set; }
        public int RecallCount { get; set; }  // times recalled in memory search, if often recalled it's probably more important
        public DateTime LastRecalled { get; set; } = DateTime.MinValue;

        [JsonIgnore] /* Stored in binary format to save space */
        public float[]? Embeddings { get; set; } // Embeddings cache
    }
    public class MemoryNamedEntity
    {
        [Instruction(skip: true)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Instruction("Name of the entity, leave this one empty if it's not clear in the conversation but use the alias instead.")]
        public string? Name { get; set; }

        [Instruction("Aliasses/indirect references how this entity is called within the text (User's dad, User's cat, etc.). Never include pronouns (I, You, Me, He, Her, ..) but always use the third person form.")]
        public List<string> Aliases { get; set; } = new();


        [Instruction("If known, generic type of this entity (Person, Animal, Book, Website, Game, Location, etc.). Leave this one empty if unknown.")]
        public string? Type { get; set; } // What is [Name] ? person/pet/book/site/etc. 

        [Instruction("If the type is a location, include the city name if known")]
        public string? LocationCity { get; set; }
        [Instruction("If the type is a location, include the country name if known")]
        public string? LocationCountry { get; set; }


        [Instruction(skip: true)]
        public MemoryNamedEntityAnalysis? Analysis { get; set; }

        public override string ToString()
        {
            var txt = "";
            if (!string.IsNullOrEmpty(Name))
            {
                txt = $"{Name}";
                if (!string.IsNullOrEmpty(LocationCity))
                    txt += $", {LocationCity}";
                if (!string.IsNullOrEmpty(LocationCountry))
                    txt += $", {LocationCountry}";

                if (!string.IsNullOrEmpty(Type))
                    txt += $" ({Type})";

                if (Aliases.Count > 0)
                {
                    txt += $", also referenced as {string.Join(",", Aliases.Take(10).Select(a => $"\"{a}\""))}";
                }
            }
            else if (!string.IsNullOrEmpty(Type) && Aliases.Count > 0)
            {
                txt = $"a {Type} referenced as {string.Join(",", Aliases.Take(10).Select(a => $"\"{a}\""))}";
            }
            else if (Aliases.Count > 0)
            {
                txt = $"an entity referenced as {string.Join(",", Aliases.Take(10).Select(a => $"\"{a}\""))}";
            }
            return txt;
        }
    }

    public class MemoryNamedEntityAnalysis
    {
        [Instruction("A detailed summary of this entity written as an introduction or explanation of this entity")]
        public string Summary { get; set; }
    }
}
