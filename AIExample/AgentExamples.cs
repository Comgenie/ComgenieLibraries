using Comgenie.AI;
using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIExample
{
    internal class AgentExamples
    {

        public static async Task AgentExample(ModelInfo model)
        {
            var llm = new LLM(model);
            var response = await llm.GenerateSolutionAsync(new List<ChatMessage>()
            {
                new ChatSystemMessage("You are a helpful assistant."),
                new ChatUserMessage()
                {
                    content = new()
                    {
                        new ChatMessageTextContent("Write a clever joke about cats and coffee, then put it in a nice html format, and finally replace all the html tags with <DOG> tags")
                    }
                }
            });

            Console.WriteLine(response?.LastChatResponse?.LastAsString());
        }
    }
}
