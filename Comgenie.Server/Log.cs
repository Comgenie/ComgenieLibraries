using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Comgenie.Server
{
    public class Log
    {
        [Flags]
        public enum LogSourceOutputSetting
        {
            Ignore=0,
            Screen=1,
            LogFile=2
        }

        private static Dictionary<string, LogSourceOutputSetting> SourceSettings = null;
        private static LogSourceOutputSetting GetSettingForSource(string source, int level)
        {
            if (SourceSettings == null)
            {
                if (File.Exists("LogSettings.json"))
                    SourceSettings = JsonSerializer.Deserialize<Dictionary<string, LogSourceOutputSetting>>(File.ReadAllText("LogSettings.json"));
                else
                    SourceSettings = new Dictionary<string, LogSourceOutputSetting>();
            }

            var key = source + ":" + level;
            if (SourceSettings.ContainsKey(key))
                return SourceSettings[key];

            return level == 0 ? LogSourceOutputSetting.Ignore : LogSourceOutputSetting.Screen;
        }
        private static void Message(ConsoleColor color, int level, string source, string message, params object[] args)
        {
            var setting = GetSettingForSource(source, level);
            if (setting.HasFlag(LogSourceOutputSetting.Screen))
            {
                Console.ForegroundColor = color;
                Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + source + ": " + message, args);
            }

            if (setting.HasFlag(LogSourceOutputSetting.LogFile))
            {                
                // TODO
            }
        }
        public static void Info(string source, string message, params object[] args)
        {
            Message(ConsoleColor.White, 1, source, message, args);            
        }
        public static void Debug(string source, string message, params object[] args)
        {
            Message(ConsoleColor.Gray, 0, source, message, args);
        }
        public static void Warning(string source, string message, params object[] args)
        {
            Message(ConsoleColor.Yellow, 2, source, message, args);
        }
        public static void Error(string source, string message, params object[] args)
        {
            Message(ConsoleColor.Red, 3, source, message, args);
        }
    }
}
