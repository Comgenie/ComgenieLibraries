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
        public async Task<InstructionFlowContext> GenerateUsingFlow(InstructionFlow flow, InstructionFlowContext? resumeFromContext=null)
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
            
            while (context.FlowPositions.Count > 0)
            {
                var currentFlow = context.Current.Flow;
                
                if (context.Current.CurrentStep >= currentFlow.Steps.Count)
                {
                    // Pop last item from stack
                    context.FlowPositions.RemoveAt(context.FlowPositions.Count - 1);
                    continue;
                }

                // Set default proceeding
                context.Current.NextStep = context.Current.CurrentStep + 1;

                // Executes the LLM actions
                await currentFlow.Steps[context.Current.CurrentStep].Execute(this, context);

                // Update current step
                context.Current.CurrentStep = context.Current.NextStep;

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
        internal List<InstructionFlowStep> Steps { get; set; } = new();
        public InstructionFlow SetSystemPrompt(string systemPrompt)
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
                    context.Messages.Add(message);
                    var resp = await llm.GenerateResponseAsync(context.Messages);
                    var answer = new InstructionFlowTextAnswer() { FlowContext = context, Text = resp?.LastAsString() };
                    answerHandler(answer);
                    
                }
            });
            return this;
        }
        public InstructionFlow AddScriptStep(string instruction, Action<InstructionFlowScriptAnswer>? answerHandler = null)
            => AddScriptStep(new ChatUserMessage(instruction), answerHandler);
        public InstructionFlow AddScriptStep(ChatUserMessage message, Action<InstructionFlowScriptAnswer>? answerHandler = null)
        {
            if (answerHandler == null)
                answerHandler = (a) => a.Proceed();

            Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    context.Messages.Add(message);
                    var resp = await llm.GenerateScriptAsync(context.Messages);
                    var answer = new InstructionFlowScriptAnswer() { FlowContext = context, Script = resp };
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
                    context.Messages.Add(message);
                    var resp = await llm.GenerateStructuredResponseAsync<T>(context.Messages);
                    var answer = new InstructionFlowStructuredAnswer<T>() { FlowContext = context, Data = resp };
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
        public Task<InstructionFlowContext> Generate()
        {
            return LLMInstance.GenerateUsingFlow(this);
        }
        public Task<InstructionFlowContext> GenerateWithContext(InstructionFlowContext resumeFromContext)
        {
            return LLMInstance.GenerateUsingFlow(this, resumeFromContext);
        }
        public async Task<ChatResponse?> GenerateResponse()
        {
            return (await LLMInstance.GenerateUsingFlow(this)).LastChatResponse;
        }

        public abstract class InstructionFlowAnswer
        {
            public required InstructionFlowContext FlowContext { get; set; }
            public void Proceed(bool includeHistory = true) 
                => FlowContext.Current.NextStep = FlowContext.Current.CurrentStep + 1; // Default
            public void Retry(string? customInstruction = null, bool includeHistory = true)
                => FlowContext.Current.NextStep = FlowContext.Current.CurrentStep;
            public void Restart(string? customInstruction = null)
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

        internal class InstructionFlowStep
        {
            public required Func<LLM, InstructionFlowContext, Task> Execute { get; set; }
        }
    }
}
