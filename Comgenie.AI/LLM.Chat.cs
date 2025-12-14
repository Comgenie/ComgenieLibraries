using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    public partial class LLM
    {
        public string PromptJson = "You are a helpful assistant that answers questions in JSON format. You are allowed to do assumptions and be creative.";
        public string PromptScript = "You are a helpful assistant that generates javascript based on the users question and instructions. You are allowed to be creative.";

        /// <summary>
        /// Simple method to ask a question to the AI.
        /// </summary>
        /// <param name="userMessage">Question to ask to AI</param>
        /// <returns>If succeeded, a LLM response object containing the assistant message.</returns>
        public async Task<ChatResponse?> GenerateResponseAsync(string userMessage, double temperature = 0.7)
        {
            return await GenerateResponseAsync(new List<ChatMessage>()
            {
                new ChatUserMessage(userMessage)
            }, temperature);
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
        public async Task<ChatResponse?> GenerateResponseAsync(List<ChatMessage> messages, double temperature = 0.7, bool addResponseToMessageList = true)
        {
            if (!addResponseToMessageList)
                messages = messages.ToList(); // This prevents external message lists from getting modified

            ChatResponse? response = null;
            while (response == null) // Default we only do one request, but tool calls might trigger multiple requests
            {
                response = await ExecuteCompletionRequest(messages, temperature, addResponseToMessageList: true);
                if (response == null)
                    return response; // Failed even after retries

                var message = response?.choices?.FirstOrDefault()?.message;

                if (message is ChatAssistantMessage assistantMessage && assistantMessage?.tool_calls != null)
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
                        Console.WriteLine("[Tool call " + tool.function.name + "] " + functionResponse);

                        // Note: The assistant tool call is already added in the messages list within the ChatExecuteRequest method

                        messages.Add(new ChatToolMessage()
                        {
                            tool_call_id = tool.function.id,
                            content = functionResponse
                        });

                        if (EnableContinuationAfterToolCall)
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
        public async Task<T?> GenerateStructuredResponseAsync<T>(string userMessage)
        {
            return await GenerateStructuredResponseAsync<T>(new List<ChatMessage>()
            {
                new ChatUserMessage(userMessage)
            });
        }

        /// <summary>
        /// Ask a question to the AI and return the result as an object of type T.
        /// This method allows you to provide a list of messages which will be used to generate a response, and which will be updated to store the raw response so it can be used to ask a follow up question.
        /// </summary>
        /// <typeparam name="T">Type of an class with Instruction attributes helping the AI to populate the fields</typeparam>
        /// <param name="prompt">Question to ask to AI</param>
        /// <param name="messages">A list which will be used for both input as adding the AI response to. If this list is empty, the required system message will be added automatically. Note that the users question is always automatically added to this list.</param>
        /// <returns>An instance of T with the populated fields</returns>
        public async Task<T?> GenerateStructuredResponseAsync<T>(List<ChatMessage> messages, double temperature = 0.7, bool includeInstructionAndExampleJson = true)
        {
            if (messages == null)
                messages = new List<ChatMessage>();

            if (messages.Count == 0)
                messages.Add(new ChatSystemMessage(PromptJson));

            if (includeInstructionAndExampleJson)
            {
                var jsonExample = JsonUtil.GenerateExampleJson<T>();

                if (messages.Last() is ChatUserMessage userMessage)
                {
                    var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                    if (textContent != null)
                        textContent.text += $"\r\n\r\nAnswer using the following JSON format:\n\n{jsonExample}";
                }
            }

            var chat = await GenerateResponseAsync(messages, temperature, addResponseToMessageList: true);

            if (chat == null || chat.choices == null || chat.choices.Count == 0)
                throw new Exception("No response from AI");

            // Check if T is list or array
            if ((typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>)) || typeof(T).IsArray)
                return chat.LastAsJsonArray<T>();

            // It's a normal object instead
            return chat.LastAsJsonObject<T>();
        }

        /// <summary>
        /// Generate a script based on the given messages and return the script as string. 
        /// </summary>
        /// <param name="messages">List of at least 1 message ending with a user message.</param>
        /// <param name="temperature">Optional: Temperature. Change to make the LLM respond more creative or not.</param>
        /// <param name="addResponseToMessageList">Optional: If set to true, the given messages list will be expanded with the assistant response and if applicable: tool responses.</param>
        /// <returns>String containing the requested script.</returns>
        public async Task<string> GenerateScriptAsync(List<ChatMessage> messages, double temperature = 0.7, bool addResponseToMessageList = true)
        {
            if (messages == null)
                messages = new List<ChatMessage>();

            if (messages.Count == 0)
            {
                messages.Add(new ChatSystemMessage(PromptScript));
            }

            var chat = await GenerateResponseAsync(messages, temperature, addResponseToMessageList);

            if (chat == null || chat.choices == null || chat.choices.Count == 0)
                throw new Exception("No response from AI");

            // TODO: Strip any script formatting tags

            return chat.LastAsString() ?? string.Empty;
        }

        /// <summary>
        /// Send a completion request with the given chat messages as payload.
        /// This method checks the cache if set, retries a few times in case of failure, and optionally follow the throttling settings.
        /// This method will not execute or handle tool calls, use GenerateResponseAsync for that.
        /// </summary>
        /// <param name="messages">List of at least 1 message.</param>
        /// <param name="temperature">Optional: Temperature. Change to make the LLM respond more creative or not.</param>
        /// <param name="addResponseToMessageList">Optional: If set to true, the given messages list will be expanded with the assistant response and if applicable: tool responses.</param>
        /// <returns>If succeeded, a LLM response object containing the assistant message.</returns>
        private async Task<ChatResponse?> ExecuteCompletionRequest(List<ChatMessage> messages, double temperature = 0.7, bool addResponseToMessageList = true)
        {
            if (DocumentVectorDB != null && DocumentAutomaticInclusionMode != DocumentReferencingMode.None && messages.Last() is ChatUserMessage userMessage)
            {
                var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                if (textContent != null)
                {
                    if (DocumentAutomaticInclusionMode == DocumentReferencingMode.FunctionCall)
                    {
                        if (!Tools.Any(a => a.Function?.Name == "retrieve_documents"))
                            AddToolCall(retrieve_documents);

                        textContent.text = $"Note: There are {DocumentVectorDB.Documents.Count} documents attached to this conversation. Use the 'retrieve_documents' function to search through them."
                            + "\r\n\r\n" + textContent.text;
                    }
                    else
                    {
                        var summary = await GenerateRelatedDocumentsSummaryAsync(textContent.text);
                        textContent.text = "Here are the related passages in the attached documents based on the user's last message:\r\n" + summary + "\r\n\r\n" + textContent.text;
                    }
                }
            } 

            if (MaxContentLength.HasValue)
                TrimOldMessages(messages, MaxContentLength.Value);

            var completionRequest = new
            {
                model = ActiveModel.Name,
                temperature = temperature,
                messages = messages,
                tools = Tools,
                tool_choice = "auto"
            };

            var txtContent = JsonSerializer.Serialize(completionRequest, new JsonSerializerOptions()
            {
                //WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            // Check if the response is cached
            if (ExistsInCacheHandler != null && this.ReadFromCacheHandler != null)
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

            return await ExecuteAndRetryHttpRequestIfFailed<ChatResponse>(async httpClient =>
            {
                Debug.WriteLine("Executing request to " + ActiveModel.ApiUrlCompletions + ", Content length: " + content.Headers.ContentLength);
                var resp = await httpClient.PostAsync(ActiveModel.ApiUrlCompletions, content);

                Debug.WriteLine($"Received {resp.StatusCode}");

                resp.EnsureSuccessStatusCode(); // Should also take care of the token rate limit exceeded error

                var str = await resp.Content.ReadAsStringAsync();

                if (!str.StartsWith("{"))
                    throw new Exception("Invalid LLM response: " + str);

                var deserialized = JsonSerializer.Deserialize<ChatResponse>(str);

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

                if (UpdateCacheHandler != null)
                {
                    var cacheKey = CalculateHash(txtContent);
                    UpdateCacheHandler(cacheKey, str);
                }

                if (addResponseToMessageList)
                    messages.Add(deserialized.choices[0].message);

                return deserialized;
            });
            
        }

        /// <summary>
        /// Remove the oldest user/tool/assistant messages to make it fit within the given max context length
        /// Note that this uses a very simple rough algorithm based on character count and assumptions
        /// </summary>
        /// <param name="messages">List of messages, note that this list will be modified, but at least 2 messages will be kept in there. System messages will not be removed.</param>
        /// <param name="maxContextLength">Max context length to try to fit to</param>
        /// <returns>Number of messages removed</returns>
        private int TrimOldMessages(List<ChatMessage> messages, long maxContextLength)
        {
            var removedMessageCount = 0;
            
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

            while (messageToToken.Count > 2 && messageToToken.Sum(a => a.Value.Item2) > maxContextLength)
            {
                // Remove the first non-system message
                var messageToRemove = messageToToken.FirstOrDefault(a => a.Value.Item1 is not ChatSystemMessage);
                if (messageToRemove == null)
                    break; // Nothing to remove anymore
                removedMessageCount++;
                messages.Remove(messageToRemove.Value.Item1);
            }

            return removedMessageCount;
        }
    }
}
