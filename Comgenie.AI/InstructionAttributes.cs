using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    public class InstructionAttribute : Attribute
    {
        public string Description { get; }
        public bool SeperateInstruction { get; }
        public bool Skip { get; set; } = false;

        /// <summary>
        /// Includes this field into the JSON example generation for the AI
        /// </summary>
        /// <param name="description">A short text describing the type of content which has to be filled in this field.</param>
        /// <param name="topLevelOnly">When set to true, this field will only be included in the example if it's on a top level and not a sub class. Use this if you want to retrieve the sub class seperately to have better control and reduced context usage.</param>
        /// <param name="skip">Do not include this field in the JSON example.</param>
        public InstructionAttribute(string description = " ... ", bool topLevelOnly = false, bool skip = false)
        {
            Description = description;
            SeperateInstruction = topLevelOnly;
            Skip = skip;
        }
    }

}
