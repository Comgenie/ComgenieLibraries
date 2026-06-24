using System;
using System.Collections.Generic;
using System.Text;

namespace Comgenie.AI.Memory
{
    internal class TextAnalyzerPresets
    {
        /// <summary>
        /// We only want Entity names and aliases which are proper 3rd person references understandable without context.
        /// </summary>
        public static string[] AliasBlacklist = new[] { "I", "you", "me", "he", "him", "she", "her", "his", "hers", "they", "them", "your", "my", "the", "this", "that" };

        /// <summary>
        /// Merge entity types to make them more generic (to prevent entities from being duplicated with different facts).
        /// </summary>
        public static string[][] SameEntityType = new string[][]
        {
            new[] { "Person", "people", "human", "ai", "assistant", "ai assistant", "ai Companion", "ai/entity" },
            new[] { "Animal", "pet", "creature", "insect", "cat", "dog", "fish" },
            new[] { "Site", "website", "webapp", "web application", "web-application", "webapplication", "web site", "site/application" },
            new[] { "Company", "organization", "employer" },
            new[] { "Location", "place", "spot" },
            new[] { "Game", "video game", "computer game", "games" },

        };
    }
}
