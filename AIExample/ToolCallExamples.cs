using Comgenie.AI;
using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIExample
{
    internal class ToolCallExamples
    {

        public static async Task ToolCallExample(ModelInfo model)
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
        public static string SetPhotoDescription([ToolCall("Clear description for the current photo", Required = true)] string newDescription)
        {
            Console.WriteLine($"Update the photo description to: " + newDescription);
            return $"Description is updated succesfully.";
        }

    }
}
