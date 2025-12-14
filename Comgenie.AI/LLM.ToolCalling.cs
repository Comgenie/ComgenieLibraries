using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    public partial class LLM
    {
        private List<ToolCallInfo> Tools { get; set; } = new();

        /// <summary>
        /// Pass a method to make available for the LLM to call.
        /// Use the ToolCall attribute to give extra information at method and parameter level.
        /// </summary>
        /// <param name="method">Reference to the method you want to make available to the LLM.</param>
        public void AddToolCall(Delegate method)
        {
            var toolInfo = ToolCallUtil.BuildToolCallInfoFromMethod(method.Method, method.Target);

            var testJson = JsonSerializer.Serialize(toolInfo, new JsonSerializerOptions()
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            Debug.WriteLine(testJson);
            Tools.Add(toolInfo);
        }

        /// <summary>
        /// Remove all existing tool calls. The next request will not have any tool calls available.
        /// </summary>
        public void ClearToolCalls()
        {
            Tools.Clear();
        }

    }
}
