using Comgenie.Server.Handlers.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HttpServerExample
{
    internal class McpExample
    {
        [McpTool("Returns the current date and time.")]
        public DateTime GetCurrentDateTime()
        {
            Console.WriteLine("Requested current date and time.");
            return DateTime.UtcNow;
        }

        [McpTool("Adds a note.")]
        public bool AddNote([McpParam("The note to add.")] string note)
        {
            Console.WriteLine("Requested to add a note: " + note);
            return true;
        }

    }
}
