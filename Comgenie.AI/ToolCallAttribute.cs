using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    public class ToolCallAttribute : Attribute
    {
        public string Description { get; set; }
        public bool Required { get; set; }
        
        public ToolCallAttribute(string description, bool required=false)
        {
            Description = description;
            Required = required;
        }
    }
}
