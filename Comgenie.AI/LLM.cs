using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Comgenie.AI
{
    public class LLM
    {
        private ModelInfo ActiveModel { get; set; }
        private Func<string, bool>? ExistsInCacheHandler { get; set; }
        private Func<string, Task<string>>? ReadFromCacheHandler { get; set; }
        private Action<string, string>? UpdateCacheHandler { get; set; }

        public double CostThisSession { get; internal set; } = 0.0;

        public int Attempts { get; set; } = 3;

        public string PromptJson = "You are a helpful assistant that answers questions in JSON format. You are allowed to do assumptions and be creative.";
        public string PromptScript = "You are a helpful assistant that generates javascript based on the users question and instructions. You are allowed to be creative.";


        // Automatic throttling
        public int MaxRequestsPerSecond { get; set; } = 10;
        public int MaxRequestsPerMinute { get; set; } = 600;

        private DateTime LastRequest { get; set; }
        private int CurrentRequestsSecond { get; set; } = 0;
        private int CurrentRequestsMinute { get; set; } = 0;

        public LLM(ModelInfo model)
        {
            ActiveModel = model;
        }
        public void SetActiveModel(ModelInfo model)
        {
            ActiveModel = model;
        }

        /// <summary>
        /// Custom cache handling.
        /// </summary>
        /// <param name="existsInCacheHandler">Function which should return true if the given parameter (key) is found in the cache</param>
        /// <param name="readFromCacheHandler">Function which returns a stream to access an item found in the cache.</param>
        /// <param name="updateCacheHandler">Action to update cache for key (first parameter) with the contents of the stream.</param>
        public void SetCache(Func<string, bool> existsInCacheHandler, Func<string, Task<string>> readFromCacheHandler, Action<string, string> updateCacheHandler)
        {
            ExistsInCacheHandler = existsInCacheHandler;
            ReadFromCacheHandler = readFromCacheHandler;
            UpdateCacheHandler = updateCacheHandler;
        }

        /// <summary>
        /// Uses Comgenie.Util.ArchiveFile to store the cache in a file + index file. This automatically sets all the correct handlers.
        /// </summary>
        /// <param name="fileName">Name of the archive file to store the cache in</param>
        public void SetCache(string fileName)
        {
            var archiveFile = new Comgenie.Util.ArchiveFile(fileName);
            ExistsInCacheHandler = (key) => archiveFile.Exists(key);
            ReadFromCacheHandler = async (key) =>
            {
                using var stream = await archiveFile.Open(key);
                using var reader = new StreamReader(stream);
                var txt = await reader.ReadToEndAsync();
                return txt;
            };
            UpdateCacheHandler = async (key, content) =>
            {
                using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                await archiveFile.Add(key, memoryStream);
            };

        }

        public static string GenerateHash(string text)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var hashBytes = sha256.ComputeHash(bytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }
        public async Task<ChatResponse?> Chat(List<ChatMessage> messages, double temperature = 0.7)
        {
            var completionRequest = new
            {
                model = ActiveModel.Name,
                temperature = temperature,
                messages = messages
            };

            var txtContent = JsonSerializer.Serialize(completionRequest);

            // Check if the response is cached
            if (ExistsInCacheHandler != null && ReadFromCacheHandler != null)
            {
                var cacheKey = GenerateHash(txtContent);
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

            for (var i = 0; i < Attempts; i++)
            {
                if (i > 0)
                    await Task.Delay(5000 * i); // Exponential backoff

                try
                {
                    using (var httpClient = new HttpClient())
                    {
                        if (ActiveModel.Type == ModelInfo.ModelType.OpenAI)
                            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ActiveModel.ApiKey);
                        else
                            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("api-key", ActiveModel.ApiKey);

                        httpClient.Timeout = new TimeSpan(4, 0, 0); // 4 hours

                        var resp = await httpClient.PostAsync(ActiveModel.ApiUrlCompletions, content);

                        resp.EnsureSuccessStatusCode(); // Should also take care of the token rate limit exceeded error

                        var str = await resp.Content.ReadAsStringAsync();

                        if (!str.StartsWith("{"))
                        {
                            Debug.WriteLine("Invalid LLM response: " + str);
                            continue;
                        }

                        var deserialized = JsonSerializer.Deserialize<ChatResponse>(str);

                        if (deserialized?.choices == null || deserialized.choices.Count == 0)
                        {
                            Debug.WriteLine("No choices in response: " + str);
                            continue;
                        }

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
                            var cacheKey = GenerateHash(txtContent);
                            UpdateCacheHandler(cacheKey, str);
                        }

                        return deserialized;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Exception retrieving LLM data:" + ex.Message);

                    if (i + 1 == Attempts)
                        throw;
                }
            }
            return null;
        }


        /// <summary>
        /// Simple method to ask a question to the AI and return the result as an object of type T.
        /// </summary>
        /// <typeparam name="T">Type of an class with Instruction attributes helping the AI to populate the fields</typeparam>
        /// <param name="prompt">Question to ask to AI</param>
        /// <returns>An instance of T with the populated fields</returns>
        public async Task<T?> Chat<T>(string prompt)
        {
            return await Chat<T>(prompt, new List<ChatMessage>());
        }

        /// <summary>
        /// Ask a question to the AI and return the result as an object of type T.
        /// This method allows you to provide a list of messages which will be used to generate a response, and which will be updated to store the raw response so it can be used to ask a follow up question.
        /// </summary>
        /// <typeparam name="T">Type of an class with Instruction attributes helping the AI to populate the fields</typeparam>
        /// <param name="prompt">Question to ask to AI</param>
        /// <param name="messages">A list which will be used for both input as adding the AI response to. If this list is empty, the required system message will be added automatically. Note that the users question is always automatically added to this list.</param>
        /// <returns>An instance of T with the populated fields</returns>
        public async Task<T?> Chat<T>(string prompt, List<ChatMessage> messages)
        {
            if (messages == null)
                messages = new List<ChatMessage>();

            if (messages.Count == 0)
            {
                messages.Add(new ChatMessage()
                {
                    role = "system",
                    content = PromptJson
                });
            }

            var jsonExample = JsonUtil.GenerateExampleJson<T>();

            var text = $"{prompt}\r\n\r\nPlease answer the following JSON format:\n\n{jsonExample}";

            messages.Add(new ChatMessage()
            {
                role = "user",
                content = text
            });

            var chat = await Chat(messages);

            if (chat == null || chat.choices == null || chat.choices.Count == 0)
                throw new Exception("No response from AI");

            messages.Add(chat.choices.First().message);

            // Check if T is list or array
            if ((typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>)) || typeof(T).IsArray)
                return chat.LastAsJsonArray<T>();

            // It's a normal object instead
            return chat.LastAsJsonObject<T>();
        }

        public async Task<string> AskScript(string prompt, List<ChatMessage> messages)
        {
            if (messages == null)
                messages = new List<ChatMessage>();

            if (messages.Count == 0)
            {
                messages.Add(new ChatMessage()
                {
                    role = "system",
                    content = PromptScript
                });
            }

            var text = $"{prompt}";

            messages.Add(new ChatMessage()
            {
                role = "user",
                content = text
            });

            var chat = await Chat(messages);

            if (chat == null || chat.choices == null || chat.choices.Count == 0)
                throw new Exception("No response from AI");

            messages.Add(chat.choices.First().message);

            // TODO: Strip any script formatting tags

            return chat.LastAsString() ?? string.Empty;
        }


        public class ChatResponse
        {
            public string? id { get; set; }
            public long created { get; set; }
            public string? model { get; set; }
            public ChatUsage? usage { get; set; }
            public List<ChatResponseChoice>? choices { get; set; }
            
            public string? LastAsString()
            {
                return choices?.LastOrDefault()?.message?.content;
            }
            public T? LastAsJsonArray<T>()
            {
                var str = LastAsString();
                if (str == null || str.IndexOf("[") < 0 || str.IndexOf("]") < 0)
                    return default(T);
                str = str.Substring(str.IndexOf("["));
                str = str.Substring(0, str.LastIndexOf("]") + 1);
                return JsonSerializer.Deserialize<T>(str);
            }
            public T? LastAsJsonObject<T>()
            {
                var str = LastAsString();
                if (str == null || str.IndexOf("{") < 0 || str.IndexOf("}") < 0)
                    return default(T);
                str = str.Substring(str.IndexOf("{"));
                str = str.Substring(0, str.LastIndexOf("}") + 1);
                return JsonSerializer.Deserialize<T>(str);
            }
            public string[]? LastAsStringArray()
            {
                return LastAsJsonArray<string[]>();
            }

            public class ChatUsage
            {
                public int prompt_tokens { get; set; }
                public int completion_tokens { get; set; }
                public int total_tokens { get; set; }
            }

            public class ChatResponseChoice
            {
                public required ChatMessage message { get; set; }
                public int index { get; set; }
                public required string finish_reason { get; set; }
            }
        }
        
        public class ChatMessage
        {
            public required string role { get; set; }
            public string? content { get; set; }
        }
    }
}
