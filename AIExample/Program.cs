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

            //AgentExample(model).Wait();
            StructuredResponseExample(model).Wait();
            DocumentExample(model).Wait();
            EmbeddingsExample(model).Wait();
            ToolCallExample(model).Wait();
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
