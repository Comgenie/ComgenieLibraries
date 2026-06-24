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
        private List<AgentConfiguration> Agents { get; set; } = new();
        public void ClearAgents()
        {
            Agents.Clear();
        }

        public AgentConfiguration AddAgent(AgentConfiguration agentConfiguration)
        {
            Agents.Add(agentConfiguration);
            return agentConfiguration;
        }
        public AgentConfiguration AddAgent(string name, string shortDescription, string systemMessage, bool canUseTools = true, bool ownContext = false)
        {
            var ac = new AgentConfiguration()
            {
                Name = name,
                ShortDescription = shortDescription,
                SystemMessage = systemMessage,
                CanUseTools = canUseTools,
                OwnContext = ownContext,
                LLMInstance = this
            };
            return AddAgent(ac);
        }
        public async Task<ChatResponse?> GenerateResponseUsingAgentsAsync(List<ChatMessage> messages, string? startWithAgentName = null, Func<string, string>? askUserHandler = null, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (generationOptions == null)
                generationOptions = DefaultGenerationOptions.Clone();

            generationOptions.IncludeAvailableTools = true;
            generationOptions.ExecuteToolCalls = true;
            generationOptions.ContinueAfterToolCalls = true;
            generationOptions.ContinueAfterToolCallsLimit = null;

            if (messages.Last() is ChatUserMessage userMessage)
            {
                var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                if (textContent == null)
                {
                    textContent = new ChatMessageTextContent() { text = "" };
                    userMessage.content.Add(textContent);
                }
                textContent.text += $"\r\n\r\n<System>You are in agent mode. Continue till the above user instruction is satisfied. You are not able to communicate with the user unless it's with the tool calls. However, anything you say will be passed to the next agent.</System>";
            }
            else
            {
                throw new Exception("Missing user message in given messages");
            }

            if (Agents.Count == 0)
                throw new Exception("No agents configured. Please add agents before calling this method.");

            var finishedWithTask = false;
            AgentConfiguration? activeAgent = null;

            var nextActiveAgent = Agents.FirstOrDefault(a => a.Name == startWithAgentName);
            if (nextActiveAgent == null)
            {
                // TODO: Use an internal 'agent selector' agent
                nextActiveAgent = Agents.First();
            }

            var agentStates = Agents.ToDictionary(a => a, a => new AgentExecutionState() {
                Agent = a,
                Messages = a.OwnContext ? messages.ToList() : messages
            });
            
            var systemMessagePostfix = "The following agents are available (Name, Description):\r\n";
            foreach (var agent in Agents)
            {
                systemMessagePostfix += $"- {agent.Name}: {agent.ShortDescription}\r\n";
            }
            systemMessagePostfix += "\r\nUse the following tool calls to control the execution loop:\r\n" +
                "- finish_execution\r\n" +
                "- transfer_to_agent\r\n" +
                //"- ask_agent\r\n" +
                (askUserHandler == null ? "" : "- ask_user\r\n");

            while (!finishedWithTask && !cancellationToken.IsCancellationRequested)
            {
                // Switch to new agent
                if (nextActiveAgent != null && nextActiveAgent != activeAgent)
                {
                    if (activeAgent?.SummarizeAfterAgentSwitch == true)
                    {
                        // Summarize current context
                        SummarizeContext(activeAgent.LLMInstance, agentStates[activeAgent].Messages);
                    }

                    if (nextActiveAgent.BeforeExecution != null)
                        nextActiveAgent.BeforeExecution();
                    activeAgent = nextActiveAgent;
                    nextActiveAgent = null;
                }
                if (activeAgent == null)
                    break;

                var agentState = agentStates[activeAgent];

                // Set correct system message
                agentState.Messages.RemoveAll(a => a is ChatSystemMessage);
                agentState.Messages.Insert(0, new ChatSystemMessage() { content = activeAgent.SystemMessage + $"\r\n\r\n{systemMessagePostfix}\r\nYou are agent \"{activeAgent.Name}\"." });

                // Refresh tools
                ClearAgentTools(activeAgent.LLMInstance);
                activeAgent.LLMInstance.AddToolCall(agentState.finish_execution);
                activeAgent.LLMInstance.AddToolCall(agentState.transfer_to_agent);
                //activeAgent.LLMInstance.AddToolCall(agentState.ask_agent);
                if (askUserHandler != null)
                    activeAgent.LLMInstance.AddToolCall(agentState.ask_user);

                // Summarize history
                if (activeAgent.SummarizeAfterMessagesCount > 0 && agentState.Messages.Count > activeAgent.SummarizeAfterMessagesCount)
                {
                    SummarizeContext(activeAgent.LLMInstance, agentState.Messages);
                }

                // Set up early stopping
                var currentRequestGenerationOptions = (activeAgent.CustomGenerationOptions ?? generationOptions).Clone();
                agentState.OnTransferToAgent = (agentName) => {
                    var agent = Agents.FirstOrDefault(a => a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase));
                    if (agent != null)
                    {
                        nextActiveAgent = agent;
                    }
                    else
                    {
                        Debug.WriteLine("Could not find agent with name " + agentName);
                        finishedWithTask = true;
                    }

                    currentRequestGenerationOptions.ContinueAfterToolCalls = false;
                };
                agentState.OnFinish = () => {
                    finishedWithTask = true;
                    currentRequestGenerationOptions.ContinueAfterToolCalls = false;
                };
                agentState.OnAskAgent = (agentName, question) =>
                {
                    var agent = Agents.FirstOrDefault(a => a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase));
                    if (agent == null)
                        return "The specified agent does not exist.";
                    return "TODO";
                };
                agentState.OnAskUser = askUserHandler;

                // Do actual request
                var response = await activeAgent.LLMInstance.GenerateResponseAsync(agentState.Messages, currentRequestGenerationOptions, cancellationToken);

                // TODO: decide nextActiveAgent
            }

            return null;

        }
        private static void SummarizeContext(LLM llmInstance, List<ChatMessage> messages)
        {
            // TODO: Keep user question but summarize all Agent work after that, keeping only the key facts and changes
        }
        private static void ClearAgentTools(LLM llm)
        {
            llm.Tools.RemoveAll(a => a.MethodInfo?.Name == "finish_execution" || a.MethodInfo?.Name == "transfer_to_agent" || a.MethodInfo?.Name == "ask_agent");
        }
        private class AgentExecutionState
        {
            public required AgentConfiguration Agent { get; set; }
            public List<ChatMessage> Messages { get; set; } = new();
            public Func<string, string>? OnAskUser { get; set; }
            public Action? OnFinish { get; set; }
            public Action<string>? OnTransferToAgent { get; set; }
            public Func<string, string, string>? OnAskAgent { get; set; }


            [ToolCall("Continue the work by another agent")]
            public void transfer_to_agent([ToolCall("Name of the agent to transfer the work to", Required = true)] string agentName)
            {
                // Mark a new agent as active agent
                if (OnTransferToAgent != null)
                    OnTransferToAgent(agentName);
            }

            [ToolCall("Ask a question to another agent.")]
            public string ask_agent([ToolCall("Name of the agent to ask a question to", Required = true)] string agentName, [ToolCall("Question to ask to the agent", Required = true)] string question)
            {
                // Keep the current (relevant) context but ask one question to another agent
                if (OnAskAgent == null)
                    return "The other agent is not available in this session.";
                return OnAskAgent(agentName, question);
            }

            [ToolCall("Declare your work finished and stop executing")]
            public void finish_execution()
            {
                if (OnFinish != null)
                    OnFinish();
            }

            [ToolCall("Ask a question to the user before continueing")]
            public string ask_user([ToolCall("Question to ask to the user", Required = true)] string question)
            {
                if (OnAskUser == null)
                    return "You are not able to communicate with the user in this session.";
                return OnAskUser(question);
            }
        }

        public class AgentConfiguration
        {
            public required string Name { get; set; } = "";
            public required string ShortDescription { get; set; } = "";
            public string SystemMessage { get; set; } = "You are a helpful assistant";
            public bool CanUseTools { get; set; } = true;
            public bool OwnContext { get; set; } = false;
            public bool SummarizeAfterAgentSwitch { get; set; } = true;
            public int SummarizeAfterMessagesCount { get; set; } = 20; 
            public LLM LLMInstance { get; set; }
            public LLMGenerationOptions? CustomGenerationOptions { get; set; } = null;

            public Action? BeforeExecution { get; set; } = null; // call back which can be used to initialize the model
        }
    }
}
