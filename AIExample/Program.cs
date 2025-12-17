using Comgenie.AI;
using Comgenie.AI.Entities;
using System.Reflection;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace AIExample
{
    internal class Program
    {
        
        static void Main(string[] args)
        {
            var model = new ModelInfo()
            {
                Name = "llama-cpp",
                ApiKey = "",
                ApiUrlCompletions = "http://127.0.0.1:8080/v1/chat/completions",
                ApiUrlEmbeddings = "http://127.0.0.1:8081/v1/embeddings",
                ApiUrlReranking = "http://127.0.0.1:8082/v1/reranking",
                CostCompletionToken = 0, // Cost per token for completion
                CostPromptToken = 0 // Cost per token for prompt
            };

            //FlowExample(model).Wait();
            //MultipleFlowExample(model).Wait();
            //AgentExample(model).Wait();
            StructuredResponseExample(model).Wait();
            DocumentExample(model).Wait();
            EmbeddingsExample(model).Wait();
            ToolCallExample(model).Wait();
        }
        static async Task FlowExample(ModelInfo model)
        {
            var llm = new LLM(model, false);

            var resp = await llm.BuildFlow()
                .SetSystemPrompt("You are JokeBot 3000")
                .AddStructuredStep<Joke>("Generate a fun joke about coffee", a => {

                    a.Data = new Joke()
                    {
                        Setup = "How does the coffee run?",
                        Punchline = "Fast!"
                    };
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
                .AddStep("Do you like this joke?")
                .GenerateResponse();
            
            Console.WriteLine("Final answer: " + resp?.LastAsString());
        }

        static async Task MultipleFlowExample(ModelInfo model)
        {
            var llm = new LLM(model);

            InstructionFlowContext? context = null;
            while (context == null || !context.Completed)
            {
                context = await llm.BuildFlow()
                    .SetSystemPrompt("You tell the horoscope of the user by being creative and making up fun theories in an engaging way. If you need more information just ask the user.")
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
                    .GenerateWithContext(context);


                if (!context.Completed) 
                {
                    // The flow stopped because the AI wants user input
                    var userPrompt = Console.ReadLine();
                    context.Messages.Add(new ChatUserMessage(userPrompt));
                }
            }
        }

        class UserHoroscopeData
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


        static async Task StructuredResponseExample(ModelInfo model)
        {
            var llm = new LLM(model);

            var joke = await llm.GenerateStructuredResponseAsync<Joke>("Generate a fun joke about coffee.");

            Console.WriteLine(joke?.Setup);
            Console.ReadLine();
            Console.WriteLine(joke?.Punchline);
        }

        public class Joke
        {
            [Instruction("A funny setup for the joke.")]
            public string Setup { get; set; } = "";

            [Instruction]
            public string Punchline { get; set; } = "";
        }


        static async Task DocumentExample(ModelInfo model)
        {
            var question = "How does the ball cat toy look?";
            var llm = new LLM(model);

            // Load a previously saved vector databse
            //await llm.LoadDocumentsVectorDataBase("test.db");

            await llm.AddDocument("notes.txt", File.ReadAllText("Room.txt"), LLM.DocumentEmbedMode.Overlapping);

            // Save the current added documents and their vectors
            // await llm.SaveDocumentsVectorDataBase("test.db", true);

            // This sets the mode how the LLM can search and access the documents
            // By default this is .FunctionCall which provides a function to call with a little bit of explanation.
            // This is recommended when supported by the model, as the LLM might want to split questions up into multiple search queries.
            // Also available: Json, XML and Markdown. These inject the related text passages based on the last message from the user in that format.
            llm.DocumentAutomaticInclusionMode = LLM.DocumentReferencingMode.FunctionCall;

            // Do the actual request. The setting above will be used to let the LLM access the documents.
            var response = await llm.GenerateResponseAsync(new List<ChatMessage>()
            {
                new ChatSystemMessage("You are a helpful assistant. If you reference sources, use the following format: [[SourceName:Offset]]"),
                new ChatUserMessage(question)
            });

            Console.WriteLine("Assistant: " + response?.LastAsString()); // Assistant: The ball cat toy is a plastic ball with a bell inside. It is decorated with images of elephants.

            // Most llm.Generate* methods can be used to ask about things in the added documents.
            var responseStructured = await llm.GenerateStructuredResponseAsync<Joke>("Tell a joke about cat #20.");
            Console.WriteLine("Joke: " + JsonSerializer.Serialize(responseStructured));

            // A very slow but extensive deep dive going through the full document:
            // var deepDiveResult = await llm.GenerateDeepDiveDocumentsResponse("What are all the colors mentioned in the documents?");
            // Console.WriteLine("Deep dive result: " + deepDiveResult);
        }

        static async Task EmbeddingsExample(ModelInfo model)
        {
            var llm = new LLM(model);

            var randomFacts = new string[]
            {
                "The Eiffel Tower can be 15 cm taller during the summer.",
                "Bananas are berries, but strawberries aren't.",
                "Honey never spoils. Archaeologists have found edible honey in ancient Egyptian tombs.",
                "Octopuses have three hearts.",
                "A group of flamingos is called a 'flamboyance'."
            };

            var x = await llm.GenerateRankingsAsync("What is something unique about the octopus?", randomFacts.ToList());

            // Generate a dummy embeddings to find out the dimension of the embeddingsvector in this model
            var embeddings = await llm.GenerateEmbeddingsAsync("dummy text");

            // Create in-memory vector database and add the facts
            using var vectorDb = new VectorDB(embeddings.Length);
            for (var i = 0; i < randomFacts.Length; i++)
            {
                var text = randomFacts[i];
                embeddings = await llm.GenerateEmbeddingsAsync(text);
                vectorDb.Upsert("Fact " + i, text, embeddings);
            }

            // Now search for the most relevant fact embeddings-wise (token distance)
            var question = "How many hearts does an octopus have?";
            embeddings = await llm.GenerateEmbeddingsAsync(question);
            var search = vectorDb.Search(embeddings, 3);

            // Now order the 3 found facts using the reranking endpoint to find the one most relevant to the question (context relevance)
            var ranked = await llm.GenerateRankingsAsync(question, search.Select(s => s.Item.ToString()).ToList());

            Console.WriteLine("Most relevant fact: \"" + ranked[0].Item + "\" with score " + ranked[0].Score);
        }

        static async Task ToolCallExample(ModelInfo model)
        {
            var llm = new LLM(model);
            llm.AddToolCall(SetPhotoDescription);

            var resp = await llm.GenerateResponseAsync(new List<ChatMessage>()
            {
                new ChatSystemMessage("You are a helpful assistant."),
                new ChatUserMessage()
                {
                    content = new()
                    {
                        new ChatMessageTextContent(
                            "Analyze the given photo and generate a good description about what is visible on the photo." +
                            "After that, pass the generated description of the given photo into the SetPhotoDescription function."),
                        new ChatMessageImageContent("./flying-motorcycle.jpg")
                    }
                }
            });

            Console.WriteLine("Assistant: " + resp?.LastAsString());
        }

        [ToolCall("Set the description for the current photo")]
        static string SetPhotoDescription([ToolCall("Clear description for the current photo", Required = true)] string newDescription)
        {
            Console.WriteLine($"Update the photo description to: " + newDescription);
            return $"Description is updated succesfully.";
        }


        static async Task AgentExample(ModelInfo model)
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

            Console.WriteLine(response?.LastAsString());
        }
    }
}
