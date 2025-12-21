using Comgenie.AI;
using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static AIExample.BasicExamples;
using static AIExample.Program;
using static Comgenie.AI.LLM;

namespace AIExample
{
    internal class FlowExamples
    {

        public static async Task FlowExample(ModelInfo model)
        {
            var llm = new LLM(model);

            var resp = await llm.BuildFlow()
                .AddSystemPromptStep("You are JokeBot 3000")
                .AddStructuredStep<Joke>("Generate a fun joke about coffee", a => {

                    Console.WriteLine(a.Data.Setup);
                    Console.WriteLine(a.Data.Punchline);

                    var jokeIsAboutCoffee =
                        a.Data.Setup.Contains("coffee", StringComparison.OrdinalIgnoreCase) ||
                        a.Data.Setup.Contains("caffeine", StringComparison.OrdinalIgnoreCase) ||
                        a.Data.Setup.Contains("beans", StringComparison.OrdinalIgnoreCase);


                    //a.Goto(llm.BuildFlow().AddStep("Tell me a good story about cookies", b => b.Proceed()));
                    //return;

                    if (!jokeIsAboutCoffee)
                        a.Retry("Make it more about coffee");
                    else
                        a.Proceed();
                })
                .AddStep("Do you like this joke? Please answer your opinion as normal text", a=> Console.WriteLine("AI: " + a.Text))
                .GenerateAsync();

        }
        public static async Task RepeatableFlowExample(ModelInfo model)
        {
            var llm = new LLM(model);

            // Simple repeatable flow example
            var resp = await llm.BuildFlow()
                .AddSystemPromptStep("You are JokeBot 3000")
                .AddStep("Write the table of contents of a book called \"My fluffy orange cat\" with 5 chapters.")
                .AddRepeatableStep("For each chapter write fun 5 sentence story.", a => Console.WriteLine("AI: " + a.Text))
                .AddStep("Generate a summary about the story", a=> Console.WriteLine("Summary: " + a.Text))
                .GenerateAsync();


            // Extensive repeatable flow example combining documents, deep dive and structured responses
            /*await llm.AddDocument("FlowExamples.cs", File.ReadAllText("FlowExamples.cs"), LLM.DocumentEmbedMode.Overlapping);

            var resp2 = await llm.BuildFlow()
                .AddOptionsStep(options =>
                {
                    options.DocumentReferencingMaxSize = 3000;
                    options.DocumentReferencingExpandBeforeCharacterCount = 50;
                    options.DocumentReferencingExpandAfterCharacterCount = 2000;
                    options.DocumentReferencingMode = DocumentReferencingMode.FunctionCallCode;
                })
                .SetSystemPrompt("You are a code assistant")
                .AddDeepDiveDocumentsStep("List all the methods within the code files including their parameters.")
                .AddRepeatableStep("Analyze each method found in your previous message.",
                    (flow, item) => flow.AddStructuredStep<MethodResult>($"Analyze at the method with the name/parameters \"{item}\" and it's inner logic and provide a good analysis report. When using the 'retrieve_documents' tool, only use the function definition as query parameter.", a => {
                        var json = JsonSerializer.Serialize(a.Data, new JsonSerializerOptions() { WriteIndented = true });
                        Console.WriteLine(json);
                    }))
                .GenerateResponseAsync();*/

        }

        public class MethodResult
        {
            [Instruction("Name of method including parameters")]
            public string Method { get; set; }

            [Instruction("A short textual description of the logic/implementation of the code within the method")]
            public string Logic { get; set; }

            [Instruction("A number between 0 (bad) to 10 (perfect) to indicate how well and readable the code is written including checks and comments where they add value")]
            public int CodeQuality { get; set; }

            [Instruction("A short improvement suggestion (if any)")]
            public string ImproveSuggestion { get; set; }
        }


        public static async Task MultipleFlowExample(ModelInfo model)
        {
            var llm = new LLM(model);

            InstructionFlowContext? context = null;
            while (context == null || !context.Completed)
            {
                context = await llm.BuildFlow()
                    .AddSystemPromptStep("You tell the horoscope of the user by being creative and making up fun theories in an engaging way. If you need more information just ask the user.")
                    .AddStep(
                        "System: Ask about the users birthdate (month + date), their favorite animal and favorite food. Only ask one question at the same time in the same message. Don't give a horoscope yet." +
                        "Say the tag [Continue] if you think that the user already mentioned all of it. Don't tell the user to say the Continue tag.", a => {

                            if (a.Text?.Contains("[Continue]", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                a.Proceed();
                                return;
                            }

                            Console.WriteLine("AI: " + a.Text);

                            // Flow example where we can directly provide an answer
                            //var userPrompt = Console.ReadLine();
                            //a.FlowContext.Messages.Add(new ChatUserMessage(userPrompt));
                            //a.Retry();

                            // Flow where we need to wait for external data and resume at a later moment
                            a.Stop();
                        })
                    .AddStructuredStep<UserHoroscopeData>("System: Format the answers from the user", a => Console.WriteLine("JSON: " + JsonSerializer.Serialize(a.Data)))
                    .AddStep("System: Please provide a fun engaging horoscope to the user based on their given details.", a => Console.WriteLine("AI: " + a.Text))
                    .GenerateWithContextAsync(context);


                if (!context.Completed)
                {
                    // The flow stopped because the AI wants user input
                    var userPrompt = Console.ReadLine();
                    context.Messages.Add(new ChatUserMessage(userPrompt));
                }
            }
        }

        public class UserHoroscopeData
        {
            [Instruction("The number of the month (1 to 12).")]
            public int BirthMonth { get; set; }
            [Instruction("The number of the date (1 to 31)")]
            public int BirthDate { get; set; }
            [Instruction]
            public string FavoriteAnimal { get; set; }
            [Instruction]
            public string FavoriteFood { get; set; }
        }


    }
}
