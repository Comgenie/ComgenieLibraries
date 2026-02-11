using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static Comgenie.AI.InstructionFlow;

namespace Comgenie.AI
{
    public partial class LLM
    {
        public InstructionFlow BuildFlow(string? name=null)
        {
            return new InstructionFlow(this, name);
        }
        public async Task<InstructionFlowContext> GenerateUsingFlowAsync(InstructionFlow flow, InstructionFlowContext? resumeFromContext=null, CancellationToken cancellationToken = default)
        {
            var context = resumeFromContext ?? new InstructionFlowContext();
            if (resumeFromContext != null)
            {
                // Reset stop flag
                resumeFromContext.FlowPositions.ForEach(a => a.StopRequested = false);

                // Assign the flow again in case the context was serialized
                resumeFromContext.FlowPositions
                    .Where(a => a.Flow == null)
                    .ToList()
                    .ForEach(a => a.Flow = flow.RelatedFlows.FirstOrDefault(b => b.Name == a.FlowName) ?? flow); // TODO: Falling back to main flow now, we might want to throw an exception instead
            }
            
            if (context.FlowPositions.Count == 0)
            {
                context.FlowPositions.Add(new InstructionFlowPositionContext() { Flow = flow, FlowName = flow.Name });
            }

            context.CancellationToken = cancellationToken;
            context.GenerationOptions = DefaultGenerationOptions;
            
            while (context.FlowPositions.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentFlow = context.Current.Flow;

                // Update current step
                context.Current.CurrentStep = context.Current.NextStep;

                // Set default proceeding
                context.Current.NextStep = context.Current.CurrentStep + 1;

                if (context.Current.CurrentStep >= currentFlow.Steps.Count)
                {
                    // Pop last item from stack
                    context.FlowPositions.RemoveAt(context.FlowPositions.Count - 1);
                    continue;
                }

                // Executes the LLM actions
                await currentFlow.Steps[context.Current.CurrentStep].Execute(this, context);

                if (context.Current.StopRequested)
                    return context;
            }

            context.Completed = true; // Indicate that we've reached the end of the flow execution (and not exited through a .Stop() call)

            return context;
        }
    }

    public class InstructionFlow
    {
        private LLM LLMInstance { get; set; }
        public string? Name { get; set; }
        public InstructionFlow(LLM llmInstance, string? name)
        {
            LLMInstance = llmInstance;
            Name = name;
        }
        internal List<InstructionFlow> RelatedFlows { get; set; } = new();
        public List<InstructionFlowStep> Steps { get; set; } = new();
        public InstructionFlow AddSystemPromptStep(string systemPrompt)
        {
            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    if (context.Messages.Count > 0 && context.Messages.First() is ChatSystemMessage systemMessage)
                        systemMessage.content = systemPrompt;
                    else
                        context.Messages.Insert(0, new ChatSystemMessage(systemPrompt));
                }
            });
            return this;
        }
        public InstructionFlow AddOptionsStep(Action<LLMGenerationOptions> changeOptionsHandler)
        {
            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    // Create a copy in case this options instance is used elsewhere 
                    context.GenerationOptions = context.GenerationOptions.Clone();
                    changeOptionsHandler(context.GenerationOptions);
                }
            });
            return this;
        }
        public InstructionFlow AddClearMessagesStep(bool includingSystemMessage=false)
        {
            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    if (includingSystemMessage)
                        context.Messages.Clear();
                    else
                        context.Messages.RemoveAll(a => a is not ChatSystemMessage);
                }
            });
            return this;
        }
        
        public InstructionFlow AddRepeatableStep(string instruction, Action<InstructionFlowTextAnswer>? answerHandler = null)
        {
            return AddRepeatableStep(instruction, null, answerHandler);
        }
        public InstructionFlow AddRepeatableStep(string instruction, Action<InstructionFlow, string>? customSubFlowHandler)
        {
            return AddRepeatableStep(instruction, customSubFlowHandler, null);
        }
        private InstructionFlow AddRepeatableStep(string instruction, Action<InstructionFlow, string>? customSubFlowHandler, Action<InstructionFlowTextAnswer>? answerHandler = null)
        {
            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    var prompt = "<UserInstruction>" + instruction + "</UserInstruction>\r\n\r\nAbove is the original user instruction to repeat an action one or more times. Please first generate the list we will enumerate through to execute the requested users action. For example if the user asks to 'do something on each file', add each file with the file specific instruction to the list.";
                    context.Messages.Add(new ChatUserMessage(prompt));
                    var listResponse = await llm.GenerateStructuredResponseAsync<RepeatableStepResponse>(context.Messages, true, context.GenerationOptions, context.CancellationToken);
                    if (listResponse != null && listResponse.List != null && listResponse.List.Count > 0)
                    {
                        
                        // Turn list into new instruction flow
                        var repeatableFlow = llm.BuildFlow("RepeatableFlow");

                        foreach (var listItem in listResponse.List)
                        {
                            if (customSubFlowHandler != null)
                            {
                                customSubFlowHandler(repeatableFlow, listItem);
                            }
                            else
                            {
                                Console.WriteLine("Adding list item: " + listItem);
                                prompt = "<UserInstruction>" + instruction + "</UserInstruction>\r\n\r\nAbove is the original user instruction to repeat an action one or more times. You've turned it into a list of items to enumerate through. You are now executing the users instruction for the item \"" + listItem + "\".";
                                repeatableFlow.AddStep(prompt, answerHandler);
                            }
                        }

                        // Creating a dummy answer to use the flow control ability to step into our newly created flow
                        var tmpAnswer = new InstructionFlowTextAnswer() { FlowContext = context, Text = "unused" };
                        tmpAnswer.Goto(repeatableFlow, 0, true);
                    }
                }
            });

            return this;
        }
        class RepeatableStepResponse
        {
            [Instruction("List as a string array")]
            public List<string> List { get; set; } = new();
        }
            
        public InstructionFlow AddStep(string instruction, Action<InstructionFlowTextAnswer>? answerHandler=null)
            => AddStep(new ChatUserMessage(instruction), answerHandler);
        public InstructionFlow AddStep(ChatUserMessage message, Action<InstructionFlowTextAnswer>? answerHandler = null)
        {
            if (answerHandler == null)
                answerHandler = (a) => a.Proceed();

            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    if (context.Messages.Count == 0 || context.Messages.Last() is not ChatUserMessage) // A message might've been provided during retry
                        context.Messages.Add(message);

                    var resp = await llm.GenerateResponseAsync(context.Messages, context.GenerationOptions, context.CancellationToken);
                    context.LastChatResponse = resp;
                    var answer = new InstructionFlowTextAnswer() { FlowContext = context, Text = resp?.LastAsString() };
                    answerHandler(answer);
                    
                }
            });
            return this;
        }
        public InstructionFlow AddStructuredStep<T>(string instruction, Action<InstructionFlowStructuredAnswer<T>>? answerHandler = null)
            => AddStructuredStep(new ChatUserMessage(instruction), answerHandler);
        public InstructionFlow AddStructuredStep<T>(ChatUserMessage message, Action<InstructionFlowStructuredAnswer<T>>? answerHandler = null)
        {
            if (answerHandler == null)
                answerHandler = (a) => a.Proceed();

            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    if (context.Messages.Count == 0 || context.Messages.Last() is not ChatUserMessage) // A message might've been provided during retry
                        context.Messages.Add(message);
                    var resp = await llm.GenerateStructuredResponseAsync<T>(context.Messages, true, context.GenerationOptions, context.CancellationToken);
                    var answer = new InstructionFlowStructuredAnswer<T>() { FlowContext = context, Data = resp };
                    answerHandler(answer);
                }
            });
            return this;
        }
        public InstructionFlow AddDeepDiveDocumentsStep(string instruction, Action<InstructionFlowTextAnswer>? answerHandler = null)
        {
            if (answerHandler == null)
                answerHandler = (a) => a.Proceed();

            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    var resp = await llm.GenerateDeepDiveDocumentsResponseAsync(instruction, generationOptions: context.GenerationOptions, cancellationToken: context.CancellationToken);
                    var answer = new InstructionFlowTextAnswer() { FlowContext = context, Text = resp };
                    context.Messages.Add(new ChatAssistantMessage() { content = resp });

                    answerHandler(answer);
                    
                }
            });
            return this;
        }
        public InstructionFlow RelatedFlow(InstructionFlow flow)
        {
            RelatedFlows.Add(flow);
            return this;
        }
        public Task<InstructionFlowContext> GenerateAsync(CancellationToken cancellationToken = default)
        {
            return LLMInstance.GenerateUsingFlowAsync(this, null, cancellationToken);
        }
        public Task<InstructionFlowContext> GenerateWithContextAsync(InstructionFlowContext resumeFromContext, CancellationToken cancellationToken = default)
        {
            return LLMInstance.GenerateUsingFlowAsync(this, resumeFromContext, cancellationToken);
        }

        /* Disabled for now as it might be too confusing
        public async Task<ChatResponse?> GenerateResponseAsync(CancellationToken? cancellationToken = null)
        {
            return (await LLMInstance.GenerateUsingFlowAsync(this, null, cancellationToken)).LastChatResponse;
        }*/

        public abstract class InstructionFlowAnswer
        {
            public required InstructionFlowContext FlowContext { get; set; }
            private void RemoveLastHistory()
            {
                // Remove last message including the user message as it will be re-added
                for (var i = FlowContext.Messages.Count - 1; i >= 0; i--)
                {
                    var message = FlowContext.Messages[i];
                    FlowContext.Messages.RemoveAt(i);
                    if (message is ChatUserMessage)
                        break;
                }
            }
            public void Proceed(bool includeHistory = true)
            {
                if (!includeHistory)
                    FlowContext.Messages.Clear();
                FlowContext.Current.NextStep = FlowContext.Current.CurrentStep + 1; // Default
            }
            public void Retry(string? customInstruction = null, bool keepFailedMessage = true)
            {
                if (!keepFailedMessage)
                    RemoveLastHistory();

                if (!string.IsNullOrEmpty(customInstruction))
                    FlowContext.Messages.Add(new ChatUserMessage(customInstruction));
                
                FlowContext.Current.NextStep = FlowContext.Current.CurrentStep;
            }
            public void Restart()
                => FlowContext.Current.NextStep = 0;
            public void Goto(int step)
                => FlowContext.Current.NextStep = step;
            public void Goto(string flowName, int flowStep = 0, bool returnToCurrentFlowAfterExecution = false)
            {
                var referencedFlow = FlowContext.Current.Flow.RelatedFlows.FirstOrDefault(a => a.Name == flowName);
                if (referencedFlow == null)
                    throw new Exception("Referenced flow " + flowName + " not found. Make sure it is referenced correctly in the building using .RelatedFlow() ");
                Goto(referencedFlow, flowStep, returnToCurrentFlowAfterExecution);
            }
            public void Goto(InstructionFlow flow, int flowStep = 0, bool returnToCurrentFlowAfterExecution = false)
            {
                if (returnToCurrentFlowAfterExecution)
                {
                    // Add to stack
                    FlowContext.Current.NextStep = FlowContext.Current.CurrentStep + 1; // Return back at the next step
                }
                else
                {
                    // Replace stack
                    FlowContext.FlowPositions.Clear();
                }

                FlowContext.FlowPositions.Add(new InstructionFlowPositionContext()
                {
                    Flow = flow,
                    FlowName = flow.Name,
                    CurrentStep = flowStep,
                    NextStep = flowStep
                });
            }

            /// <summary>
            /// Stops the execution of this flow. Use the context (returned from flow.Generate() ) to continue execution where this flow left off.
            /// Note: This one resumes from this same step. Use .StopAtNext() to resume from the next step.
            /// </summary>
            public void Stop()
            {
                FlowContext.Current.NextStep = FlowContext.Current.CurrentStep;
                FlowContext.Current.StopRequested = true;
            }

            /// <summary>
            /// Stops the execution of this flow. Use the context (returned from flow.Generate() ) to continue execution where this flow left off.
            /// Note: This one resumes from the next step. Use .Stop() to resume from the current step.
            /// </summary>
            public void StopAtNext()
            {
                FlowContext.Current.NextStep = FlowContext.Current.CurrentStep + 1;
                FlowContext.Current.StopRequested = true;
            }
        }


        public class InstructionFlowTextAnswer : InstructionFlowAnswer
        {
            public required string? Text { get; set; }
        }
        public class InstructionFlowStructuredAnswer<T> : InstructionFlowAnswer
        {
            public required T? Data { get; set; }
        }
        public class InstructionFlowScriptAnswer : InstructionFlowAnswer
        {
            public required string? Script { get; set; }
        }

        public class InstructionFlowStep
        {
            public required Func<LLM, InstructionFlowContext, Task> Execute { get; set; }
        }
    }
}
