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
        /// Generate a plan and form an instruction flow based on the given users instructions.
        /// This flow can be seen as a plan to fulfill the users instruction but will not directly be executed.
        /// Note that this may generate flows which expand upon itself during execution based on information retrieved during execution.
        /// </summary>
        /// <param name="messages">List of messages, requiring at least 1 user message</param>
        /// <param name="generationOptions">Optional: Custom generation options, uses .DefaultGenerationOptions if not set</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel the flow generation</param>
        /// <returns>Generated instruction flow if succeeded</returns>
        public async Task<InstructionFlow?> GenerateSolutionFlowAsync(List<ChatMessage> messages, LLMGenerationOptions? generationOptions = null, CancellationToken? cancellationToken = null)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (!messages.Any(a=>a is ChatUserMessage))
                return null;

            if (messages.Last() is ChatUserMessage userMessage)
            {
                var jsonExample = JsonUtil.GetExampleJson<AgentExecutionPlan>();

                var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                if (textContent != null)
                    textContent.text = $"<UserInstruction>\r\n{textContent.text}\r\n</UserInstruction>\r\n\r\nYou are in agent mode now. Above is the original user prompt. Please make a full plan for each step to do the requested action or answer the given question. Return this plan in the following JSON structure:\r\n{jsonExample}";
            }

            var plan = await GenerateStructuredResponseAsync<AgentExecutionPlan>(messages, false, generationOptions, cancellationToken);

            if (plan?.Steps == null)
                return null;

            var refinePlanMessages = new List<ChatMessage>();
            refinePlanMessages.Add(new ChatSystemMessage(
                "You are an assistant refining a previously generated plan. " +
                "Without any previous context you'll look at a given plan and add any missing information with properties present in the result JSON.\r\n" +
                "For the step Type property the Type should be set to Collection if any of this is true given the step Instruction:\r\n" +
                "- An instruction is doing something on 'each' element in a list.\r\n" +
                "- Generally if 'each', 'for every', 'iterate over' or 'for all' is mentioned within the instruction.\r\n"));
            refinePlanMessages.Add(new ChatUserMessage($"You are refining the following plan:\r\n{JsonSerializer.Serialize(plan)}"));

            var refinedPlan = await GenerateStructuredResponseAsync<RefinedAgentExecutionPlan>(refinePlanMessages, true, generationOptions, cancellationToken);

            var flow = BuildFlow();

            foreach (var step in refinedPlan.Steps)
            {
                if (step.Type != null && step.Type.Contains("collection", StringComparison.OrdinalIgnoreCase))
                {
                    flow.AddRepeatableStep(step.Instruction, a =>
                    {
                        // TODO: Ask if the execution plan should be modified
                        a.Proceed();
                    });
                }
                else
                {
                    flow.AddStep(step.Instruction, a =>
                    {
                        // TODO: Ask if the execution plan should be modified
                        a.Proceed();
                    });
                }
            }

            return flow; 

        }
        /// <summary>
        /// Generate a plan and go through the generated plan. This can be seen as 'Agent mode'.
        /// </summary>
        /// <param name="messages">List of messages, requiring at least 1 user message</param>
        /// <returns>The last assistant response from the LLM</returns>
        public async Task<InstructionFlowContext?> GenerateSolutionAsync(List<ChatMessage> messages, LLMGenerationOptions? generationOptions = null, CancellationToken? cancellationToken = null)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions;

            if (messages.Count == 0)
                return null;

            var flow = await GenerateSolutionFlowAsync(messages, generationOptions, cancellationToken);
            if (flow == null)
                return null;

            var response = await flow.GenerateAsync();

            return response;
        }

        private class AgentExecutionPlan
        {
            [Instruction("A full list of steps within this execution plan")]
            public List<AgentExecutionPlanStep> Steps { get; set; }
        }
        private class AgentExecutionPlanStep
        {
            [Instruction("A well written concise instruction for this step")]
            public string Instruction { get; set; }
        }

        private class RefinedAgentExecutionPlan
        {
            [Instruction("A full list of steps within this execution plan")]
            public List<RefinedAgentExecutionPlanStep> Steps { get; set; }
        }
        private class RefinedAgentExecutionPlanStep
        {
            [Instruction("A well written concise instruction for this step")]
            public string Instruction { get; set; }

            [Instruction("The type 'Operation' for regular steps exeuting a simple operation. The type 'Collection' for any instructions with any type of iterating (for each .. etc).")]
            public string Type { get; set; }
        }
    }
}
