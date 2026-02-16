using Comgenie.AI.Entities;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comgenie.AI
{
    public partial class LLM
    {
        
        /// <summary>
        /// Simple method to ask a question to the AI.
        /// </summary>
        /// <param name="userMessage">Question to ask to AI</param>
        /// <returns>If succeeded, a LLM response object containing the assistant message.</returns>
        public async Task<ChatResponse?> GenerateResponseAsync(string userMessage, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            return await GenerateResponseAsync(new List<ChatMessage>()
            {
                new ChatUserMessage(userMessage)
            }, generationOptions, cancellationToken);
        }

        /// <summary>
        /// Send a completion request with the given chat messages as payload.
        /// This method checks the cache if set, retries a few times in case of failure, and optionally follow the throttling settings.
        /// This method handles tool calls and continuations after tool calls if enabled.
        /// </summary>
        /// <param name="messages">List of at least 1 message.</param>
        /// <param name="temperature">Optional: Temperature. Change to make the LLM respond more creative or not.</param>
        /// <param name="addResponseToMessageList">Optional: If set to true, the given messages list will be expanded with the assistant response and if applicable: tool responses.</param>
        /// <returns>If succeeded, a LLM response object containing the assistant message.</returns>
        public async Task<ChatResponse?> GenerateResponseAsync(List<ChatMessage> messages, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (!generationOptions.AddResponseToMessageList)
                messages = messages.ToList(); // This prevents external message lists from getting modified

            ChatResponse? response = null;
            var requestsLeft = generationOptions.ContinueAfterToolCallsLimit ?? Int32.MaxValue;

            while (response == null) // Default we only do one request, but tool calls might trigger multiple requests
            {
                if (requestsLeft-- <= 0)
                    break;

                response = await ExecuteCompletionRequest(messages, generationOptions, cancellationToken);
                if (response == null)
                    return response; // Failed even after retries

                var message = response?.choices?.FirstOrDefault()?.message;

                if (generationOptions.ExecuteToolCalls && message is ChatAssistantMessage assistantMessage && assistantMessage?.tool_calls != null)
                {
                    // Execute tool call
                    foreach (var tool in assistantMessage.tool_calls)
                    {
                        if (tool.function == null)
                            continue;

                        var toolInfo = Tools.FirstOrDefault(t => t.Function?.Name == tool.function.name);
                        if (toolInfo == null)
                        {
                            Debug.WriteLine("Warning, tool call for unknown tool: " + tool.function.name);
                            continue;
                        }

                        var functionResponse = ToolCallUtil.ExecuteFunction(toolInfo, tool.function.arguments);
                        Debug.WriteLine("[Tool call " + tool.function.name + "] " + functionResponse);

                        // Note: The assistant tool call is already added in the messages list within the ChatExecuteRequest method
                        messages.Add(new ChatToolMessage()
                        {
                            tool_call_id = tool.function.id,
                            content = functionResponse
                        });

                        if (generationOptions.ContinueAfterToolCalls)
                            response = null; // This automatically executes the next request
                    }
                }
            }
            return response;
        }

        /// <summary>
        /// Simple method to ask a question to the AI and return the result as an object of type T.
        /// </summary>
        /// <typeparam name="T">Type of an class with Instruction attributes helping the AI to populate the fields</typeparam>
        /// <param name="userMessage">Question to ask to AI</param>
        /// <returns>An instance of T with the populated fields</returns>
        public async Task<T?> GenerateStructuredResponseAsync<T>(string userMessage, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            return await GenerateStructuredResponseAsync<T>(new List<ChatMessage>()
            {
                new ChatUserMessage(userMessage)
            }, true, generationOptions, cancellationToken);
        }

        /// <summary>
        /// Ask a question to the AI and return the result as an object of type T.
        /// This method allows you to provide a list of messages which will be used to generate a response, and which will be updated to store the raw response so it can be used to ask a follow up question.
        /// </summary>
        /// <typeparam name="T">Type of an class with Instruction attributes helping the AI to populate the fields</typeparam>
        /// <param name="messages">A list which will be used for both input as adding the AI response to. If this list is empty, the required system message will be added automatically.</param>
        /// <param name="includeInstructionAndExampleJson">If set to true, an explanation of the JSON structure of T will be injected in the last user message.</param>
        /// <param name="generationOptions">Optional: Options to control the LLM generation and behaviour within this method. If not set the .DefaultGenerationOptions is used.</param>
        /// <returns>An instance of T with the populated fields</returns>
        public async Task<T?> GenerateStructuredResponseAsync<T>(List<ChatMessage> messages, bool includeInstructionAndExampleJson = true, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (includeInstructionAndExampleJson)
            {
                var jsonExample = JsonUtil.GetExampleJson<T>();

                if (messages.Last() is ChatUserMessage userMessage)
                {
                    var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                    if (textContent != null)
                        textContent.text += $"\r\n\r\nAnswer to this instruction using the following JSON format:\n\n{jsonExample}";
                }
            }

            // We will retry a few times as some LLM models aren't good at following the json syntax
            bool extraReminder = false;
            for (var i = 0; i < generationOptions.FailedRequestRetryCount; i++)
            {
                try
                {
                    var chat = await GenerateResponseAsync(messages, generationOptions, cancellationToken);

                    if (chat == null || chat.choices == null || chat.choices.Count == 0)
                        throw new Exception("No response from AI");

                    // Check if T is list or array
                    if ((typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>)) || typeof(T).IsArray)
                        return chat.LastAsJsonArray<T>();

                    // It's a normal object instead
                    return chat.LastAsJsonObject<T>();
                }
                catch (Exception)
                {
                    if (messages.LastOrDefault() is ChatAssistantMessage assistantMessage)
                        messages.Remove(assistantMessage);

                    if (!extraReminder && messages.LastOrDefault() is ChatUserMessage userMessage) {
                        var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                        if (textContent != null)
                        {
                            textContent.text += $"\r\n\r\nMake sure to return a valid JSON object without any added formatting or comments.";
                            extraReminder = true;
                        }
                    }

                    if (i + 1 == generationOptions.FailedRequestRetryCount)
                        throw;
                }
            }

            return default(T);
        }


        /// <summary>
        /// Send a completion request with the given chat messages as payload.
        /// This method checks the cache if set, retries a few times in case of failure, and optionally follow the throttling settings.
        /// This method will not execute or handle tool calls, use GenerateResponseAsync for that.
        /// </summary>
        /// <param name="messages">List of at least 1 message.</param>
        /// <param name="generationOptions">Optional: Options to control the LLM generation and behaviour within this method. If not set the .DefaultGenerationOptions is used.</param>
        /// <returns>If succeeded, a LLM response object containing the assistant message.</returns>
        private async Task<ChatResponse?> ExecuteCompletionRequest(List<ChatMessage> messages, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (DocumentVectorDB != null && generationOptions.DocumentReferencingMode != DocumentReferencingMode.None && messages.Last() is ChatUserMessage userMessage)
            {
                var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                if (textContent != null)
                {
                    if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.FunctionCallDocuments)
                    {
                        if (!Tools.Any(a => a.Function?.Name == "retrieve_documents"))
                            AddToolCall(retrieve_documents);

                        textContent.text = $"Note: There are {DocumentVectorDB.Documents.Count} documents attached to this conversation. Use the 'retrieve_documents' function to search through them."
                            + "\r\n\r\n" + textContent.text;
                    }
                    else if (generationOptions.DocumentReferencingMode == DocumentReferencingMode.FunctionCallCode)
                    {
                        if (!Tools.Any(a => a.Function?.Name == "retrieve_code"))
                            AddToolCall(retrieve_code);

                        textContent.text = $"Note: There are {DocumentVectorDB.Documents.Count} code files/documents attached to this conversation. Use the 'retrieve_documents' function to search through them."
                            + "\r\n\r\n" + textContent.text;
                    }
                    else
                    {
                        var summary = await GenerateRelatedDocumentsSummaryAsync(textContent.text, generationOptions, cancellationToken);
                        if (!string.IsNullOrEmpty(summary))
                            textContent.text = $"{generationOptions.DocumentReferencingAddedInstruction}:\r\n" + summary + "\r\n\r\n" + textContent.text;
                    }
                }
            } 

            if (MaxContentLength.HasValue)
                TrimOldMessages(messages, MaxContentLength.Value - 1000, generationOptions); // Space for response
            var completionRequest = new Dictionary<string, object>();
            completionRequest["model"] = ActiveModel.Name;
            completionRequest["temperature"] = generationOptions.Temperature;
            completionRequest["messages"] = messages;
            if (generationOptions.IncludeAvailableTools)
            {
                completionRequest["tools"] = Tools;
                completionRequest["tool_choice"] = "auto";
            }

            if (generationOptions.StopEarlyTextSequences != null)
                completionRequest["stop"] = generationOptions.StopEarlyTextSequences;

            foreach (var item in generationOptions.ExtraRequestParameters)
            {
                completionRequest[item.Key] = item.Value;
            }

            var txtContent = JsonSerializer.Serialize(completionRequest, new JsonSerializerOptions()
            {
                //WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            // Check if the response is cached
            if (generationOptions.UseCacheIfAvailable && ExistsInCacheHandler != null && this.ReadFromCacheHandler != null)
            {
                var cacheKey = CalculateHash(txtContent);
                if (ExistsInCacheHandler(cacheKey))
                {
                    var cachedResponse = await ReadFromCacheHandler(cacheKey);
                    if (cachedResponse != null)
                        return JsonSerializer.Deserialize<ChatResponse>(cachedResponse);
                }
            }

            // Throttle requests to avoid hitting rate limits
            var first = true;
            while (first || CurrentRequestsSecond >= MaxRequestsPerSecond || CurrentRequestsMinute >= MaxRequestsPerMinute)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cur = DateTime.UtcNow;
                if (cur.Second != LastRequest.Second)
                    CurrentRequestsSecond = 0;
                if (cur.Minute != LastRequest.Minute)
                    CurrentRequestsMinute = 0;

                if (!first)
                    await Task.Delay(50);
                first = false;
            }
            CurrentRequestsSecond++;
            CurrentRequestsMinute++;
            LastRequest = DateTime.UtcNow;

            var content = new StringContent(txtContent, Encoding.UTF8, "application/json");

            return await ExecuteAndRetryHttpRequestIfFailedAsync<ChatResponse>(async (httpClient, requestCancellationToken) =>
            {
                Debug.WriteLine("Executing request to " + ActiveModel.ApiUrlCompletions + ", Content length: " + content.Headers.ContentLength);
                var resp = await httpClient.PostAsync(ActiveModel.ApiUrlCompletions, content, requestCancellationToken);

                Debug.WriteLine($"Received {resp.StatusCode}");

                resp.EnsureSuccessStatusCode(); // Should also take care of the token rate limit exceeded error

                var str = await resp.Content.ReadAsStringAsync(requestCancellationToken);

                if (!str.StartsWith("{"))
                    throw new Exception("Invalid LLM response: " + str);

                var deserialized = JsonSerializer.Deserialize<ChatResponse>(str, new JsonSerializerOptions()
                {
                    AllowOutOfOrderMetadataProperties = true // OpenAI places the 'role' property at the end of the object
                });

                if (deserialized?.choices == null || deserialized.choices.Count == 0 || deserialized.choices[0].message == null)
                    throw new Exception("No choices in response: " + str);

                // Update total costs
                if (deserialized.usage != null)
                {
                    var cost = ((double)deserialized.usage.prompt_tokens * ActiveModel.CostPromptToken) +
                        ((double)deserialized.usage.completion_tokens * ActiveModel.CostCompletionToken);
                    CostThisSession += cost;

                    Debug.WriteLine($"Cost this session: {CostThisSession} (prompt: {deserialized.usage.prompt_tokens}, completion: {deserialized.usage.completion_tokens})");
                }

                if (generationOptions.UseCacheIfAvailable && UpdateCacheHandler != null)
                {
                    var cacheKey = CalculateHash(txtContent);
                    UpdateCacheHandler(cacheKey, str);
                }

                if (generationOptions.AddResponseToMessageList)
                {
                    if (messages.Count > 0 && messages.Last() is ChatAssistantMessage lastAssistantMessage && deserialized.choices[0].message is ChatAssistantMessage newAssistantMessage)
                    {
                        // If we've provided the initial answer, then we will add the rest to the same message
                        lastAssistantMessage.content += newAssistantMessage.content;

                        if (generationOptions.StopEarlyTextSequences != null && generationOptions.StopEarlyTextSequences.Length == 1)
                            lastAssistantMessage.content += generationOptions.StopEarlyTextSequences[0];
                    }
                    else
                    {
                        messages.Add(deserialized.choices[0].message);
                    }
                }

                return deserialized;
            }, generationOptions, cancellationToken);
        }

        /// <summary>
        /// Remove the oldest user/tool/assistant messages to make it fit within the given max context length
        /// Note that this uses a very simple rough algorithm based on character count and assumptions
        /// </summary>
        /// <param name="messages">List of messages, note that this list will be modified, but at least 2 messages will be kept in there. System messages will not be removed.</param>
        /// <param name="maxContextLength">Max context length to try to fit to</param>
        /// <returns>Number of messages removed</returns>
        private int TrimOldMessages(List<ChatMessage> messages, long maxContextLength, LLMGenerationOptions generationOptions)
        {
            var removedMessageCount = 0;
            List<(ChatMessage, long)?> messageToToken = EstimateTokenCount(messages);

            var currentContextLength = messageToToken.Sum(a => a.Value.Item2);

            bool dontUseCustomTrimHandler = false;
            while (messageToToken.Count > 2 && currentContextLength > maxContextLength)
            {
                // Attempt to shrink the used context length using any custom provided handler
                if (generationOptions.OnTrimMessages != null && !dontUseCustomTrimHandler)
                {
                    generationOptions.OnTrimMessages(messages, maxContextLength);
                    var newMessageToToken = EstimateTokenCount(messages); // Call it again in case the custom trim handler modifies content
                    var newContextSize = newMessageToToken.Sum(a => a.Value.Item2);
                    if (newContextSize >= currentContextLength)
                    {
                        // Custom method didn't reduce the size so fall back to the default algorithm
                        dontUseCustomTrimHandler = true;
                    }
                    currentContextLength = newContextSize;
                    messageToToken = newMessageToToken;
                    continue;
                }

                // Remove the first non-system message
                var messageToRemove = messageToToken.FirstOrDefault(a => a.Value.Item1 is not ChatSystemMessage);
                if (messageToRemove == null)
                    break; // Nothing to remove anymore
                removedMessageCount++;
                messageToToken.Remove(messageToRemove);
                if (!messages.Remove(messageToRemove.Value.Item1))
                    break;

                currentContextLength = messageToToken.Sum(a => a.Value.Item2);
            }

            return removedMessageCount;
        }
        private List<(ChatMessage, long)?> EstimateTokenCount(List<ChatMessage> messages)
        {
            List<(ChatMessage, long)?> messageToToken = new();
            for (var i = 0; i < messages.Count; i++)
            {
                long estimatedTokenUsage = 0;
                if (messages[i] is ChatSystemMessage systemMessage)
                    estimatedTokenUsage += systemMessage.content.Length + 20;
                else if (messages[i] is ChatAssistantMessage assistantMessage)
                {
                    if (assistantMessage.content != null)
                        estimatedTokenUsage += assistantMessage.content.Length + 20;
                    if (assistantMessage.tool_calls != null && assistantMessage.tool_calls.Where(a => a.function != null).Count() > 0)
                        estimatedTokenUsage += assistantMessage.tool_calls.Where(a => a.function != null).Sum(a => a.function!.name.Length + a.function!.arguments.Length);
                }
                else if (messages[i] is ChatUserMessage userMessage2)
                {

                    for (var j = 0; j < userMessage2.content.Count; j++)
                    {
                        if (userMessage2.content[j] is ChatMessageTextContent messageContent)
                            estimatedTokenUsage += messageContent.text.Length + 20;
                        else if (userMessage2.content[j] is ChatMessageImageContent imageContent)
                            estimatedTokenUsage += 2048; // Rough estimation
                    }

                }
                else if (messages[i] is ChatToolMessage toolMessage)
                {
                    estimatedTokenUsage += toolMessage.content.Length;
                }
                messageToToken.Add((messages[i], estimatedTokenUsage));
            }
            return messageToToken;
        }
    }
}
