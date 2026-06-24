using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Comgenie.AI.Memory.Entities
{
    public class TextAnalysis
    {
        [Instruction("A concise summary of the text (max a few sentences), always include important facts, names, locations, agreements and explained things")]
        public string? Summary { get; set; }

        [Instruction]
        public List<TextAnalysisFact> ImportantFacts { get; set; } = new();

        [Instruction("Indicates whether the text contains sensitive information that should be handled with extra care (true or false)")]
        public bool IsSenstive { get; set; } = false;
        [Instruction("The general (emotional) vibe of this text described in one word")]
        public string? Vibe { get; set; }
        [Instruction("Tags associated with this text for easier searching later")]
        public List<string> Tags { get; set; } = new();
    }
    public class TextAnalysisFact
    {
        [Instruction("Most important things told in the text which are useful to remember. Make sure each memory contains enough context to fully understand it.")]
        public string Text { get; set; }

        [Instruction("Is this a hard fact, a promise, an event, something from a fictional/fantasy story, code inspection or something else? Choose between: Fact/Promise/Event/Fictional/Code/Other")]
        public string Type { get; set; }

        [Instruction("Is this a long term durable fact or promise (true)? or transient context like greetings (false)? ")]
        public bool IsDurable { get; set; }

        [Instruction("Is this relevant after 24 hours? true/false")]
        public bool IsLongRelevant { get; set; }

    }

}
