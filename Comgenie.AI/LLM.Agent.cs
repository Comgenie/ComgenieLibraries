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
        /// <summary>
        /// Generate a plan and run the LLM in agent mode.
        /// </summary>
        /// <param name="messages">List of at least 1 message ending with a user message.</param>
        /// <param name="temperature">Optional: Temperature. Change to make the LLM respond more creative or not.</param>
        /// <param name="addResponseToMessageList">Optional: If set to true, the given messages list will be expanded with the assistant response and if applicable: tool responses.</param>
        /// <param name="assistantCallBack">Optional: For every assistant answer this callback will be called. During agent mode multiple assistant responses will be called.</param>
        /// <returns>The last assistant response from the LLM</returns>
        public async Task<ChatResponse?> GenerateSolutionAsync(List<ChatMessage> messages)
        {
            if (messages.Count == 0)
                return null;

            if (messages.Last() is ChatUserMessage userMessage)
            {
                var jsonExample = JsonUtil.GenerateExampleJson<AgentExecutionPlan>();

                var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                if (textContent != null)
                    textContent.text = $"<UserInstruction>\r\n{textContent.text}\r\n</UserInstruction>\r\n\r\nYou are in agent mode now. Above is the original user prompt. Please make a full plan for each step to do the requested action or answer the given question. Return this plan in the following JSON structure:\r\n{jsonExample}";
            }


            var response = await GenerateStructuredResponseAsync<AgentExecutionPlan>(messages);
            // TODO: Generate a plan (which might we expanded while running) and run through the plan.
            // Make sure the plan also support looping through items like 'Retrieve list of chapters', 'For each chapter, summarize'

            // For code we want it to build up a full architecture with all classes and method names including comments.
            //   And then (with just the meta data) generate the full code for each method one by one. 
            //   If the AI misses a method while generating one, stop the generation and first generate the missing method, then restart the previous one.
            return null;
        }

        private class AgentExecutionPlan
        {
            [Instruction("A full list of steps within this execution plan")]
            public List<AgentExecutionPlanStep> Steps { get; set; }
        }
        private class AgentExecutionPlanStep
        {
            [Instruction("A well written concise instruction for this step")]
            public string StepInstruction { get; set; }
        }
    }
}
