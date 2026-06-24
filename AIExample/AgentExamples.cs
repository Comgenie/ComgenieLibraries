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
            llm.AddAgent("JokeAgent", "An agent that is good at making jokes", "You are a joke making assistant. You are very good at making clever and funny jokes about any topic. After making a joke you should review it with a text reviewer agent.", true, false);
            llm.AddAgent("TextReviewerAgent", "An agent that is good at reviewing text", "You are a text reviewer assistant. You are very good at reviewing text and providing feedback on how to improve it.", true, true);

            var response = await llm.GenerateResponseUsingAgentsAsync(new List<ChatMessage>()
            {
                new ChatUserMessage()
                {
                    content = new()
                    {
                        new ChatMessageTextContent("Write a clever joke about cats and coffee")
                    }
                }
            });
            Console.WriteLine(response?.LastAsString());
        }
    }
}
