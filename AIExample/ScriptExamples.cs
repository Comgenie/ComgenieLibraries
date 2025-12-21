using Comgenie.AI;
using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIExample
{
    internal class ScriptExamples
    {
        public static async Task ScriptExample(ModelInfo model)
        {
            var llm = new LLM(model);

            // Example where the LLM generates a normal response but uses a generated script to provide an accurate answer
            var messages = new List<ChatMessage>();
            messages.Add(new ChatUserMessage("What is 42*9001? And how many r's are there in the word strawberrrrrrrrrrry?"));
            var resp = await llm.GenerateResponseUsingScriptAsync(messages);
            Console.WriteLine("AI: " + resp?.LastAsString());

            // Example with access to a tool call
            llm.AddToolCall(SetLightColor);
            var resp2 = await llm.GenerateResponseUsingScriptAsync("Change the light to red and blue each second, do this for a couple of seconds.");
            Console.WriteLine("AI: " + resp2?.LastAsString());
        }
        public static void SetLightColor(int r, int g, int b)
        {
            Console.WriteLine("[Setting light color to " + r + "," + g + "," + b + "]");
        }

    }
}
