using Acornima.Ast;
using Comgenie.AI.Entities;
using Jint.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Threading;
using static Comgenie.AI.InstructionFlow;
using Jint;

namespace Comgenie.AI
{
    /// <summary>
    /// Extension methods to offer script generation capabilities to LLM instances.
    /// </summary>
    public static class LLMScriptingExtensions
    {

        /// <summary>
        /// Generate a script based on the given messages and return the script as string. 
        /// </summary>
        /// <param name="messages">List of at least 1 message ending with a user message. Note, this method will always expand this message list.</param>
        /// <param name="interactiveGeneration">Inject console log and statement outputs into the generated javascript to help the assistant during generation. Note that this executes the statements directly using Jint.</param>
        /// <param name="generationOptions">Generation options to use. If null, the default generation options of the LLM instance will be used. Note that some settings will be overridden when interactiveGeneration is true.</param>
        /// <param name="cancellationToken">Cancellation token to stop the generation process and script evaluation.</param>
        /// <returns>String containing the requested script.</returns>
        public static async Task<string> GenerateScriptAsync(this LLM llm, List<ChatMessage> messages, bool interactiveGeneration = false, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            if (generationOptions == null)
                generationOptions = llm.DefaultGenerationOptions;

            if (interactiveGeneration)
            {
                generationOptions = generationOptions.Clone(); // We'll be modifying settings so make sure to do it in a copy
                generationOptions.StopEarlyTextSequences = new string[] { "\n" };
                generationOptions.DocumentReferencingMode = LLM.DocumentReferencingMode.None;
            }

            string PromptScript = "You are a helpful assistant that generates javascript based on the users question and instructions. You are allowed to be creative.";

            var ScriptInstruction = "Scripts are constructed as javascript, but limited to only native javascript functions and the functions listed below. " +
                "Note that you are running in interactive mode and the results of the scripts and console log outputs are directly given back to you. Use reasoning to determine the next steps. Make sure to include the javascript formatting tag in your response.\r\n" +
                "The available functions are:\r\nconsole.log('Example message');\r\nconsole.wait(SECONDS);\r\n";

            var console = new ConsoleMethods();

            // TODO: Find out a way to set the mysteriously missing CancellationToken for script execution

            var engine = new Jint.Engine(options =>
            {
                options.CancellationToken(cancellationToken);
            }).SetValue("console", console);

            if (generationOptions.IncludeAvailableTools)
            {
                foreach (var tool in llm.Tools)
                {
                    if (tool.Function == null || tool.MethodDelegate == null)
                        continue;
                    var parameterStr = "...";
                    if (tool.Function.Parameters is ToolCallObjectParameterInfo objectParameters)
                    {
                        parameterStr = string.Join(", ", objectParameters.Properties.Select(a => a.Key));
                    }
                    ScriptInstruction += $"tools_{tool.Function.Name}({parameterStr});  (See tool definition)\r\n";
                    engine.SetValue($"tools_{tool.Function.Name}", tool.MethodDelegate);
                }
            }
            generationOptions.IncludeAvailableTools = false; // Make sure the LLM uses the tools within the script, and not calls them

            try
            {
                if (messages == null)
                    messages = new List<ChatMessage>();

                if (messages.Count == 0)
                {
                    messages.Add(new ChatSystemMessage(PromptScript));
                }

                if (messages.Last() is ChatUserMessage userMessage)
                {
                    var textContent = userMessage.content.FirstOrDefault(a => a is ChatMessageTextContent) as ChatMessageTextContent;
                    if (textContent != null)
                        textContent.text += $"<UserInstruction>{textContent.text}</UserInstruction>\r\n\r\n<ScriptingInstruction>{ScriptInstruction}</ScriptingInstruction>\r\n\r\nAbove is the original user instruction and scripting instructions. Generate a script to fulfill the users instruction. When finished with the script say the tag [STOP] once";
                }

                ChatResponse? chat;
                var scriptToExecute = "";

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    chat = await llm.GenerateResponseAsync(messages, generationOptions, cancellationToken);

                    if (chat == null || chat.choices == null || chat.choices.Count == 0)
                        throw new Exception("No response from AI");

                    if (!interactiveGeneration)
                        break;

                    if (chat.choices[0].finish_reason == "stop") // stop because of text sequence
                    {
                        if (messages.Last() is ChatAssistantMessage assistantMessage)
                        {
                            if (assistantMessage.content.Contains("[STOP]"))
                            {
                                assistantMessage.content = assistantMessage.content.Replace("[STOP]", "");
                                break;
                            }

                            var script = ExtractScriptFromLLMMessage(assistantMessage.content).TrimEnd('\r', '\n');
                            var message = assistantMessage.content.TrimEnd('\r', '\n');

                            // Compare last line of script to last line of message to check if the LLM is currently generating a script
                            var pos = script.LastIndexOf("\n");
                            var lastLineScript = pos >= 0 ? script.Substring(pos + 1) : script;

                            pos = message.LastIndexOf("\n");
                            var lastLineMessage = pos >= 0 ? message.Substring(pos + 1) : message;

                            if (!string.IsNullOrEmpty(lastLineScript) && lastLineMessage == lastLineScript)
                            {
                                // Currently generating a script, execute the last line and return output if applicable
                                Console.WriteLine("Script line: " + lastLineScript);
                                scriptToExecute += lastLineScript + "\r\n";
                                
                                cancellationToken.ThrowIfCancellationRequested();

                                try
                                {
                                    // During generation we might've not completed a while loop, this checks the syntax
                                    Jint.Engine.PrepareScript(scriptToExecute);
                                }
                                catch
                                {
                                    continue;
                                }

                                try
                                {
                                    Console.WriteLine("Executing: " + scriptToExecute);
                                    console.Output = "";

                                    var value = engine.Evaluate(scriptToExecute);

                                    scriptToExecute = "";

                                    if (!string.IsNullOrEmpty(console.Output))
                                    {
                                        Console.WriteLine(console.Output);
                                        assistantMessage.content += console.Output + "\r\n";
                                    }

                                    if (value != null && value.ToObject() != null && value.ToObject()?.ToString() != "null")
                                    {
                                        Console.WriteLine("// Output: " + GetObjectAsText(value.ToObject()) + "\r\n");
                                        assistantMessage.content += "// Output: " + GetObjectAsText(value.ToObject()) + "\r\n";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("// Exception: " + ex.Message + "\r\n");
                                    assistantMessage.content += "// Exception: " + ex.Message + "\r\n";
                                }
                            }
                            continue;
                        }
                    }
                }

                if (messages.Count > 0 && messages.Last() is ChatAssistantMessage asisstantMessage)
                    return ExtractScriptFromLLMMessage(asisstantMessage.content);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error during script generation: " + e);
                throw;
            }

            return "";
        }

        /// <summary>
        /// Generates a chat response by first creating a script from the provided messages and then using that script
        /// to formulate a response with the language model.
        /// </summary>
        /// <param name="llm">The language model instance used to generate the script and the final response. Cannot be null.</param>
        /// <param name="messages">A list of chat messages, should end with an ChatUserMessage with an instruction.</param>
        /// <param name="generationOptions">Generation options to use. If null, the default generation options of the LLM instance will be used. </param>
        /// <param name="cancellationToken">A cancellation token that can be used to cancel the generation request and script execution.</param>
        /// <returns>Chat response containing the assistant final response.</returns>
        public static async Task<ChatResponse?> GenerateResponseUsingScriptAsync(this LLM llm, List<ChatMessage> messages, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            var script = await GenerateScriptAsync(llm, messages, true, generationOptions, cancellationToken);

            messages.Add(new ChatUserMessage("Using the above script and output, answer the original user instruction using normal text."));
            var resp = await llm.GenerateResponseAsync(messages, generationOptions, cancellationToken);

            return resp;
        }

        /// <summary>
        /// Generates a chat response by first creating a script from the provided messages and then using that script
        /// to formulate a response with the language model.
        /// </summary>
        /// <param name="llm">The language model instance used to generate the script and the final response. Cannot be null.</param>
        /// <param name="instruction">User instruction for the llm, will be used to generate a script and the final answer</param>
        /// <param name="generationOptions">Generation options to use. If null, the default generation options of the LLM instance will be used. </param>
        /// <param name="cancellationToken">A cancellation token that can be used to cancel the generation request and script execution.</param>
        /// <returns>Chat response containing the assistant final response.</returns>
        public static async Task<ChatResponse?> GenerateResponseUsingScriptAsync(this LLM llm, string instruction, LLMGenerationOptions? generationOptions = null, CancellationToken cancellationToken = default)
        {
            var messages = new List<ChatMessage>();
            messages.Add(new ChatUserMessage(instruction));
            return await GenerateResponseUsingScriptAsync(llm, messages);
        }

        /// <summary>
        /// Execute a previously generated script using Jint. It has access to all tool calls and internal helper functions which were also available during script generation.
        /// </summary>
        /// <param name="llm">LLM instance with (optionally) added tool calls</param>
        /// <param name="script">Script excluding any formatting tags</param>
        /// <param name="cancellationToken">Cancellation token to stop the script execution.</param>
        /// <returns>Output of the last statement within the script.</returns>
        public static async Task<ScriptResult> ExecuteScriptAsync(this LLM llm, string script, CancellationToken cancellationToken = default)
        {
            var console = new ConsoleMethods();
            var engine = new Jint.Engine(options =>
            {
                options.CancellationToken(cancellationToken);
            }).SetValue("console", console);
            
            foreach (var tool in llm.Tools)
            {
                if (tool.Function == null || tool.MethodDelegate == null)
                    continue;
                engine.SetValue("tools_" + tool.Function.Name, tool.MethodDelegate);
            }

            var result = engine.Evaluate(script);

            if (!string.IsNullOrEmpty(console.Output))
                Debug.WriteLine(console.Output);

            return new ScriptResult
            {
                LastStatementValue = result.ToObject(),
                ConsoleOutput = console.Output
            };
        }

        public class ScriptResult
        {
            public object? LastStatementValue { get; set; }
            public string? ConsoleOutput { get; set; }
        }

        /// <summary>
        /// Add a step to this instruction flow to generate a script.
        /// </summary>
        /// <param name="flow">The flow to add this script step to</param>
        /// <param name="message">User message containing the instruction to generate a script</param>
        /// <param name="answerHandler">Optional: Handler to do something with the answer and control flow progression</param>
        /// <returns>This flow with the added script step</returns>
        public static InstructionFlow AddScriptStep(this InstructionFlow flow, ChatUserMessage message, Action<InstructionFlowScriptAnswer>? answerHandler = null)
        {
            if (answerHandler == null)
                answerHandler = (a) => a.Proceed();

            flow.Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    if (context.Messages.Count == 0 || context.Messages.Last() is not ChatUserMessage) // A message might've been provided during retry
                        context.Messages.Add(message);

                    var resp = await llm.GenerateScriptAsync(context.Messages, true, context.GenerationOptions, context.CancellationToken);
                    var answer = new InstructionFlowScriptAnswer() { FlowContext = context, Script = resp };
                    answerHandler(answer);
                }
            });

            return flow;
        }

        /// <summary>
        /// Add a step to this instruction flow to generate a script and then answer a question using that script.
        /// </summary>
        /// <param name="flow">The flow to add this script step to</param>
        /// <param name="message">User message containing the instruction to generate a script/answer</param>
        /// <param name="answerHandler">Optional: Handler to do something with the final answer and control flow progression</param>
        /// <returns>This flow with the added script step</returns>
        public static InstructionFlow AddResponseUsingScriptStep(this InstructionFlow flow, ChatUserMessage message, Action<InstructionFlowTextAnswer>? answerHandler = null)
        {
            if (answerHandler == null)
                answerHandler = (a) => a.Proceed();

            flow.Steps.Add(new InstructionFlowStep()
            {
                Execute = async (llm, context) =>
                {
                    if (context.Messages.Count == 0 || context.Messages.Last() is not ChatUserMessage) // A message might've been provided during retry
                        context.Messages.Add(message);

                    var resp = await llm.GenerateResponseUsingScriptAsync(context.Messages, context.GenerationOptions, context.CancellationToken);
                    var answer = new InstructionFlowTextAnswer() { FlowContext = context, Text = resp?.LastAsString() };
                    answerHandler(answer);
                }
            });
            return flow;
        }

        private static string GetObjectAsText(object? a)
        {
            if (a == null)
                return "null";

            var valueAsJson = System.Text.Json.JsonSerializer.Serialize(a);
            if (valueAsJson.Length > 200)
                valueAsJson = valueAsJson.Substring(0, 200) + " ... (truncated)";

            return valueAsJson;
        }
        private class ScriptTools
        {
            private LLM llm { get; set; }
            public ScriptTools(LLM llm)
            {
                this.llm = llm;
            }

        }
        private class ConsoleMethods
        {
            public string Output { get; set; }
            public void log(object a)
            {
                Output += "// Console log: " + GetObjectAsText(a) + "\r\n";
            }
            public void log(object a, object b)
            {
                Output += "// Console log: " + GetObjectAsText(a) + ", " + GetObjectAsText(b) + "\r\n";
            }
            public void log(object a, object b, object c)
            {
                Output += "// Console log: " + GetObjectAsText(a) + ", " + GetObjectAsText(b) + ", " + GetObjectAsText(c) + "\r\n";
            }
            public void log(object a, object b, object c, object d)
            {
                Output += "// Console log: " + GetObjectAsText(a) + ", " + GetObjectAsText(b) + ", " + GetObjectAsText(c) + ", " + GetObjectAsText(d) + "\r\n";
            }
            public void log(object a, object b, object c, object d, object e)
            {
                Output += "// Console log: " + GetObjectAsText(a) + ", " + GetObjectAsText(b) + ", " + GetObjectAsText(c) + ", " + GetObjectAsText(d) + ", " + GetObjectAsText(e) + "\r\n";
            }
            public void wait(int seconds)
            {
                Thread.Sleep(seconds * 1000);
            }
        }

        private static string ExtractScriptFromLLMMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            const string CodeBlockMarker = "```";
            int startMarkerIndex = message.IndexOf(CodeBlockMarker);
            if (startMarkerIndex == -1)
                return string.Empty;

            int endOfOpeningLineIndex = message.IndexOf('\n', startMarkerIndex);

            // Edge case: If the stream cuts off at "```java" with no newline yet, there is no content.
            if (endOfOpeningLineIndex == -1)
                return string.Empty;

            int contentStartIndex = endOfOpeningLineIndex + 1;
            int endMarkerIndex = message.IndexOf(CodeBlockMarker, contentStartIndex);

            if (endMarkerIndex != -1)
            {
                // CASE A: The message is complete (Closing tag found)
                int length = endMarkerIndex - contentStartIndex;

                // Trim() is useful here to remove the trailing newline often present before the closing ```
                return message.Substring(contentStartIndex, length).TrimEnd();
            }
            else
            {
                // CASE B: Partial message (Start tag exists, but end tag is missing)
                // Return everything from the start of the content to the end of the current string.
                // We do NOT trim the end here, because the LLM might be in the middle of writing a line.
                if (contentStartIndex < message.Length)
                {
                    return message.Substring(contentStartIndex);
                }

                return string.Empty;
            }
        }


    }
}
