using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Comgenie.AI.Entities
{
    public class ChatResponse
    {
        public string? id { get; set; }
        public long created { get; set; }
        public string? model { get; set; }
        public ChatUsage? usage { get; set; }
        public List<ChatResponseChoice>? choices { get; set; }

        public string? LastAsString()
        {
            var lastMessage = choices?.LastOrDefault()?.message;
            if (lastMessage is ChatAssistantMessage assistantMessage)
                return assistantMessage.content;
            return null;
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
}
