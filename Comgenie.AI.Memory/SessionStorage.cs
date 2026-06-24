using Comgenie.AI.Memory.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Comgenie.AI.Memory
{
    public class SessionStorage
    {
        public List<Session> Sessions { get; set; } = new(); // Conversations, Texts, etc.
        public List<State> States { get; set; } = new();

        public Memory Memory { get; set; } = new();

    }
}
