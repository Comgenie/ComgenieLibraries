using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    internal class WorkUtil
    {
        public static int WorkThreadCount = 10;
        private static BlockingCollection<Action> Work = new BlockingCollection<Action>();
        private static List<Thread> WorkThreads = new List<Thread>();

        public static void Do(Action task)
        {
            if (WorkThreads.Count == 0)
            {
                for (var i = 0; i < WorkThreadCount; i++)
                {
                    var thread = new Thread(new ThreadStart(() =>
                    {
                        WorkThread();
                    }));
                    WorkThreads.Add(thread);
                    thread.Start();
                }
            }

            Work.Add(task);
        }


        private static void WorkThread()
        {
            foreach (var work in Work.GetConsumingEnumerable())
            {
                if (work == null)
                    return; // Secret signal to shut up
                work.Invoke();
            }
        }
        private static void StopAllWorkThreads()
        {
            for (var i = 0; i < WorkThreads.Count; i++)
                Work.Add(null); // Send the secret signal to shut up

            for (var i = 0; i < WorkThreads.Count; i++)
                WorkThreads[i].Join();
            WorkThreads.Clear();
        }

    }
}
