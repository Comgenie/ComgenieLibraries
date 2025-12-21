using Comgenie.AI;
using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIExample
{
    internal class BasicExamples
    {
        public static async Task NormalResponseExample(ModelInfo model)
        {
            var llm = new LLM(model);

            var fact = await llm.GenerateResponseAsync("What is a cool fact about cats?");

            Console.WriteLine(fact?.LastAsString());
        }
        public static async Task StructuredResponseExample(ModelInfo model)
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

    }
}
