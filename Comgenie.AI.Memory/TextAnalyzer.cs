using Comgenie.AI;
using Comgenie.AI.Entities;
using Comgenie.AI.Memory.Entities;
using Comgenie.AI.Memory.Utils;
using Microsoft.VisualBasic;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Comgenie.AI.Memory
{
    /// <summary>
    /// Contains the methods to take conversations/other data and expand an existing memory with new facts and entities
    /// </summary>
    public class TextAnalyzer
    {
        public static async Task<TextAnalysis?> ProcessTextAsync(LLM llm, string text, string textType, string source = "Conversation", Memory? memory = null, DateTime? currentDate = null, string? memoryGroupType = null, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (memory != null)
                await memory.InitializeMemoryDbAsync(llm);

            if (currentDate == null)
                currentDate = DateTime.Now;

            // Useful to have good consistent json results.
            generationOptions = (generationOptions ?? llm.DefaultGenerationOptions).Clone(a =>
            {
                a.Temperature = 0.8f;
                //a.ExtraRequestParameters["min_p"] = 0.1;
                a.ExtraRequestParameters["repeat_penalty"] = 1.0;
                a.ExtraRequestParameters["presence_penalty"] = 0.0;
                a.ExtraRequestParameters["dry_multiplier"] = 0.5;
                a.IncludeAvailableTools = false;
                a.EnableRequestModifiers = false;
                a.ExtraRequestParameters["chat_template_kwargs"] = new { enable_thinking = false };
            });
            
            var msg = new List<ChatMessage>()
            {
                new ChatSystemMessage($"You are an assistant analyzing and extracting key information to remember from a {textType}. Source: {source}"),
                new ChatUserMessage($"Here is the {textType}:\r\n{text}\r\n\r\nAnalyze the {textType} above. " +
                $"Note that the facts will be added as individual pieces of memory without the context of the {textType}, so make sure they are meaningful and complete.\r\n" +
                $"Other requirements:\r\n" +
                $"- Add as many facts as you can based on this text.\r\n" +
                $"- Always include any references to named entities (in third person, no pronouns) within the facts, even if it sounds robotic.\r\n" +
                $"- If any day, time or moment is mentioned (today, tomorrow, next week etc.), include it within the memory.\r\n" +
                $"- Extract facts, not conversational events. Memories should only contain important facts worth remembering.\r\n" +
                $"- Don't mention 'conversation', 'day of the conversation' or anything referencing the conversation itself within the memories.\r\n")
            };

            var analysis = await llm.GenerateStructuredResponseAsync<TextAnalysis>(msg, generationOptions: generationOptions, cancellationToken: cancellationToken);
            
            if (analysis == null)
                return null;

            if (memory == null)
                return analysis; 

            // Convert all 'relative dates'. LLM's seems to be strangly bad at even formatting them so we will do it instead.
            List<RelativeDateHelper.TextWithAbsoluteDate> memoriesWithAbsoluteDates = new();
            
            await Parallel.ForAsync(0, analysis.ImportantFacts.Count, async (i, innerCancellationToken) =>
            {
                // We don't care about any 'conversational events'
                if (!analysis.ImportantFacts[i].IsLongRelevant)
                    return;
                if (analysis.ImportantFacts[i].Type.Equals("Fictional", StringComparison.OrdinalIgnoreCase))
                    return; // Not interested in anything 'made up' (It will still be in the conversation summary probably)

                var memoryWithAbsoluteDate = await RelativeDateHelper.ConvertTextWithRelativeDates(llm, currentDate.Value, analysis.ImportantFacts[i].Text, generationOptions);
                memoriesWithAbsoluteDates.Add(memoryWithAbsoluteDate);
                analysis.ImportantFacts[i].Text = memoryWithAbsoluteDate.Text; // update original as the analysis will be saved at the conversation as well
            });

            // Get all entities mentioned in the conversation
            var msgWithAnalysis = msg.ToList();
            var genericEntityTypes = TextAnalyzerPresets.SameEntityType.Select(a => a[0]).ToList();
            msg.Add(new ChatUserMessage($"Are there any named entities ({string.Join(", ", genericEntityTypes)}) in the {textType}? Make sure to never include pronouns but only use references which makes sense even without the context. For aliases make sure to only include clear obvious aliases which cannot be confused with someone or something else. Make sure to return valid JSON."));
            var entities = await llm.GenerateStructuredResponseAsync<List<MemoryNamedEntity>>(msg, generationOptions: generationOptions, cancellationToken: cancellationToken);
            if (entities == null)
                return null;
            Console.WriteLine("Entities: " + entities.Count);
            
            // Merge entities with the ones we've already known about
            var mergedEntities = new List<MemoryNamedEntity>();
            foreach (var entity in entities)
            {
                // Remove pronouns to really make sure we won't save those
                entity.Aliases.RemoveAll(a => TextAnalyzerPresets.AliasBlacklist.ContainsAny(a.Split(' '), StringComparer.OrdinalIgnoreCase));

                if (entity.Aliases.Count == 0 && string.IsNullOrEmpty(entity.Name))
                    continue;

                for (int i = 0; entity.Type != null && i < TextAnalyzerPresets.SameEntityType.Length; i++)
                {
                    for (int j = 1; j < TextAnalyzerPresets.SameEntityType[i].Length; j++)
                    {
                        if (entity.Type.Equals(TextAnalyzerPresets.SameEntityType[i][j], StringComparison.OrdinalIgnoreCase))
                        {
                            entity.Type = TextAnalyzerPresets.SameEntityType[i][0];
                        }
                    }
                }

                // Find merge candidate
                var existing = memory.FindSimilarNamedEntity(entity);
                if (existing != null)
                {
                    // Merge
                    if (string.IsNullOrEmpty(existing.Name))
                        existing.Name = entity.Name;
                    if (string.IsNullOrEmpty(existing.Type))
                        existing.Type = entity.Type;
                    if (string.IsNullOrEmpty(existing.LocationCity))
                        existing.LocationCity = entity.LocationCity;
                    if (string.IsNullOrEmpty(existing.LocationCountry))
                        existing.LocationCountry = entity.LocationCountry;

                    // Our name might've been different, we will add it as alias
                    if (!string.IsNullOrEmpty(entity.Name) && !entity.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))
                        entity.Aliases.Add(entity.Name);

                    foreach (var alias in entity.Aliases)
                    {
                        if (!existing.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                            existing.Aliases.Add(alias);
                    }

                    mergedEntities.Add(existing);
                }
                else
                {
                    // New entity
                    memory.NamedEntities.Add(entity);
                    mergedEntities.Add(entity);
                }
            }

            // Create memories with all associated entities

            // Method 1: Include the list of entities in the convo (now with all the known names and aliases)
            //  And then just go through each of the memories to assign them to the right named entities
            StringBuilder sb = new StringBuilder();
            foreach (var entity in mergedEntities)
            {
                sb.AppendLine($"- {entity}");
            }

            var memoryEntries = new List<MemoryEntry>();
            var missingEntities = new ConcurrentDictionary<string, List<MemoryEntry>>();
            
            await Parallel.ForEachAsync(memoriesWithAbsoluteDates, cancellationToken, async (memoryText, innerCancellationToken) =>
            {
                var memoryEntry = new MemoryEntry()
                {
                    Source = source,
                    Content = memoryText.Text,
                    EarliestMentionedDate = memoryText.EarliestMentionedDate,
                    LatestMentionedDate = memoryText.LatestMentionedDate
                };
                memoryEntries.Add(memoryEntry);

                var copyMsg = msgWithAnalysis.ToList();
                copyMsg.Add(new ChatUserMessage($"Here are all the known named entities within this {textType}:\r\n" +
                    $"{sb.ToString()}\r\n\r\n" +
                    $"Based on the {textType} and list of entities above, what entities should the following memory be attached to?\r\nMemory: {memoryText.Text}"));

                var relatedEntitiesNames = await llm.GenerateStructuredResponseAsync<List<string>>(copyMsg, generationOptions: generationOptions, cancellationToken: innerCancellationToken);
                if (relatedEntitiesNames == null)
                    relatedEntitiesNames = new List<string>(); // The show must go on

                foreach (var relatedEntityName in relatedEntitiesNames)
                {
                    var relatedEntity = mergedEntities.FirstOrDefault(a => a.Name != null && a.Name.Equals(relatedEntityName, StringComparison.OrdinalIgnoreCase));
                    if (relatedEntity == null)
                        relatedEntity = mergedEntities.FirstOrDefault(a => a.Aliases != null && a.Aliases.Contains(relatedEntityName, StringComparer.OrdinalIgnoreCase));
                    if (relatedEntity == null)
                        relatedEntity = mergedEntities.FirstOrDefault(a => a.Name != null && a.Name.StartsWith(relatedEntityName, StringComparison.OrdinalIgnoreCase));
                    if (relatedEntity == null) // Situations where location might be added
                        relatedEntity = mergedEntities.FirstOrDefault(a => a.ToString().StartsWith(relatedEntityName, StringComparison.OrdinalIgnoreCase));

                    if (relatedEntity == null && relatedEntityName.Contains(","))
                    {
                        var shortRelatedEntityName = relatedEntityName.Substring(0, relatedEntityName.IndexOf(","));

                        relatedEntity = mergedEntities.FirstOrDefault(a => a.Name != null && a.Name.Equals(shortRelatedEntityName, StringComparison.OrdinalIgnoreCase));
                        if (relatedEntity == null)
                            relatedEntity = mergedEntities.FirstOrDefault(a => a.Aliases != null && a.Aliases.Contains(shortRelatedEntityName, StringComparer.OrdinalIgnoreCase));
                        if (relatedEntity == null)
                            relatedEntity = mergedEntities.FirstOrDefault(a => a.Name != null && a.Name.StartsWith(shortRelatedEntityName, StringComparison.OrdinalIgnoreCase));
                    }


                    if (relatedEntity == null)
                    {
                        Console.WriteLine("Entity mentioned not found: " + relatedEntityName);
                        if (!missingEntities.ContainsKey(relatedEntityName))
                            missingEntities[relatedEntityName] = new List<MemoryEntry>();
                        missingEntities[relatedEntityName].Add(memoryEntry);
                        continue;
                    }

                    Console.WriteLine("Attaching memory to " + relatedEntity);
                    memoryEntry.AssociatedNamedEntities.Add(relatedEntity.Id);
                }
            });

            // TODO: Add missing entities and associate the memories (pass 2)

            // Merge memories with existing memories
            foreach (var memoryEntry in memoryEntries)
            {
                var existingMemory = await memory.FindSimilarMemoryAsync(llm, memoryEntry);
                if (existingMemory != null)
                {
                    msg = new List<ChatMessage>()
                    {
                        new ChatSystemMessage($"You are an assistant helping with merging or separating multiple texts."),
                        new ChatUserMessage(
                            $"Text 1:\r\n{existingMemory.Content}\r\n\r\n" +
                            $"Text 2:\r\n{memoryEntry.Content}\r\n\r\n" +
                            $"Compare the two texts above. If they are similar and not contradicting, combine them and return a summarized version (don't duplicate information). If they are contradicting set the Contradicting field to true and leave the MergedText empty.")
                    };

                    var mergeResponse = await llm.GenerateStructuredResponseAsync<MergeResult>(msg, generationOptions: generationOptions, cancellationToken: cancellationToken);
                    if (mergeResponse != null)
                    {
                        if (mergeResponse.Contradicting)
                        {
                            // TODO: We might want to mark some things as unchangable facts

                            // We will decrease the certainty score for the existing one
                            // but we also use the MergedCount to decide how fast it goes down.
                            // If we've merged this item many times in the past, it must've been quite accurate.
                            existingMemory.CertaintyScore = Math.Max(-1, existingMemory.CertaintyScore - (0.25 / (existingMemory.MergedCount + 1)));
                            await memory.UpsertMemoryAsync(llm, existingMemory);

                            memoryEntry.CertaintyScore = 1; // Note, we believe it for now if it's the last thing we've heard.
                            await memory.UpsertMemoryAsync(llm, memoryEntry);
                        }
                        else if (!string.IsNullOrEmpty(mergeResponse.MergedText))
                        {
                            existingMemory.Content = mergeResponse.MergedText;
                            existingMemory.CertaintyScore = Math.Min(1, existingMemory.CertaintyScore + 0.25);
                            existingMemory.MergedCount++;
                            existingMemory.LastUpdated = DateTime.UtcNow;

                            var newAssociatedEntities = memoryEntry.AssociatedNamedEntities.Where(a => !existingMemory.AssociatedNamedEntities.Contains(a));
                            existingMemory.AssociatedNamedEntities.AddRange(newAssociatedEntities);
                            
                            await memory.UpsertMemoryAsync(llm, existingMemory);
                        }
                        else
                        {
                            Console.WriteLine("Unexpected result, ignoring memory");
                        }
                    }
                }
                else
                {
                    // New memory!
                    await memory.UpsertMemoryAsync(llm, memoryEntry);
                }
            }

            // Update entity summary with all the known facts
            //foreach (var entity in mergedEntities)
            await Parallel.ForEachAsync(mergedEntities, cancellationToken, async (entity, innerCancellationToken) =>
            {
                var associatedMemories = memory.Entries.Where(a => a.AssociatedNamedEntities != null && a.AssociatedNamedEntities.Contains(entity.Id)).OrderBy(a => a.LastUpdated).ToList();
                if (!associatedMemories.Any())
                    return;

                var sbEntity = new StringBuilder();
                for (var i = associatedMemories.Count - 1; i >= 0; i--)
                {
                    sbEntity.AppendLine($"- [{associatedMemories[i].Source}] {associatedMemories[i].Content}");
                    if (sbEntity.Length >= 5000)
                        break;
                }

                msg = new List<ChatMessage>()
                {
                    new ChatSystemMessage($"You are an assistant describing (named) entities based on it's associated memories. It can be persons, locations, games, etc."),
                    new ChatUserMessage(
                        $"Entity:\r\n{entity}\r\n\r\n" +
                        $"Associated memories:\r\n{sbEntity.ToString()}\r\n\r\n" +
                        $"Based on the information above, give an introduction for this entity. Focus on the entity \"{entity}\" and not any related entities. You may reference related entities by name and their relationships/interaction with the main entity. Note: Only include information that you know based on the associated memories. Do not say what you don't know. Use 1 to 5 sentences.")
                };

                var entityAnalysis = await llm.GenerateStructuredResponseAsync<MemoryNamedEntityAnalysis>(msg, generationOptions: generationOptions, cancellationToken: innerCancellationToken);
                if (entityAnalysis != null)
                {
                    entity.Analysis = entityAnalysis;
                }

            });

            return analysis;
        }
        private class MergeResult
        {
            [Instruction("Merged/summarized result without any formatting or html tags")]
            public string? MergedText { get; set; }
            [Instruction("Set to true if the two texts are contradicting")]
            public bool Contradicting { get; set; }
        }

        public static string ExtractTranscript(List<ChatMessage> messages, string assistantName, string conversationPartner)
        {
            StringBuilder chatLog = new StringBuilder();
            foreach (var conv in messages)
            {
                if (conv is ChatUserMessage userMessage)
                {
                    var text = ((ChatMessageTextContent?)userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent))?.text ?? "";
                    chatLog.AppendLine(conversationPartner + ": " + RemoveAddedTags(text));
                }
                else if (conv is ChatAssistantMessage assistantMessage)
                {
                    chatLog.AppendLine(assistantName + ": " + assistantMessage.content);
                }
            }
            return chatLog.ToString();
        }

        public static string RemoveAddedTags(string text)
        {
            // Remove all html-like tags and it's inner text
            string pairedTagsPattern = @"<([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>.*?</\1>";
            text = Regex.Replace(text, pairedTagsPattern, string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return text.Trim(' ', '\r', '\n', '\t');
        }
        private static Regex HtmlRemoverRegex = new Regex("<(.*?)>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        public static string RemoveAllTags(string text)
        {
            // Remove all html-like tags and it's inner text
            text = HtmlRemoverRegex.Replace(text, string.Empty);
            return text.Trim(' ', '\r', '\n', '\t');
        }
    }
}
