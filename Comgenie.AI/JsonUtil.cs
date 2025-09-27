using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    public class JsonUtil
    {
        /// <summary>
        /// Generate an example JSON string to help the AI understand the desired response structure.
        /// Use the InstructionAttribute to provide additional descriptions for each property.
        /// </summary>
        /// <typeparam name="T">Serializable type to generate an example structure for</typeparam>
        /// <returns>Textual representation of T showing the structure and additional instructions</returns>
        public static string GenerateExampleJson<T>()
        {
            return GenerateExampleJson(typeof(T), 1);
        }

        private static string GenerateExampleJson(Type type, int level = 1)
        {
            if (level > 5)
                return "{}"; // Prevent infinite recursion for deeply nested objects

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (type.GetGenericArguments()[0] == typeof(string))
                    return "[ \"Result 1\", \"Result 2\", ... ]";
                return "[ " + GenerateExampleJson(type.GetGenericArguments()[0], level) + ", ... ]";
            }

            // Generate a JSON example based on the properties of the object, while looking at the AskAttribute of those properties.
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            var properties = type.GetProperties();
            var spaces = new string(' ', level * 2);
            foreach (var prop in properties)
            {
                var askAttr = prop.GetCustomAttributes(typeof(InstructionAttribute), false).FirstOrDefault() as InstructionAttribute;
                if (askAttr != null && !askAttr.Skip && (level == 1 || !askAttr.SeperateInstruction))
                {
                    // Check if property is a list or array
                    if (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        if (prop.PropertyType.GetGenericArguments()[0] == typeof(string))
                        {
                            sb.AppendLine($"{spaces}\"{prop.Name}\": [");
                            sb.AppendLine($"{spaces}  \"{askAttr.Description}\"");
                            sb.AppendLine($"{spaces}],");
                        }
                        else
                        {
                            sb.AppendLine($"{spaces}\"{prop.Name}\": [");
                            sb.AppendLine($"{spaces}  /* {askAttr.Description} */ ");
                            sb.AppendLine($"{spaces}  {GenerateExampleJson(prop.PropertyType.GetGenericArguments()[0], level + 2)}, ...");
                            sb.AppendLine($"{spaces}],");
                        }
                    }
                    else if (prop.PropertyType.IsArray)
                    {
                        sb.AppendLine($"{spaces}\"{prop.Name}\": [");
                        sb.AppendLine($"{spaces}  \"{askAttr.Description}\"");
                        sb.AppendLine($"{spaces}],");
                        /*}
                        else if (prop.PropertyType == typeof(DateTime) || prop.PropertyType == typeof(DateTime?))
                        {
                            sb.AppendLine($"  \"{prop.Name}\": \"yyyy-MM-dd\", * {askAttr.Description} * ");*/
                        // Check if the property is a sub object
                    }
                    else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string))
                    {
                        // Recursive call to the GenerateExampleJson object to handle nested objects
                        var subJson = GenerateExampleJson(prop.PropertyType, level + 1);
                        sb.AppendLine($"{spaces}\"{prop.Name}\": {subJson},");
                    }
                    else
                    {
                        sb.AppendLine($"{spaces}\"{prop.Name}\": \"{askAttr.Description}\",");
                    }
                }
            }
            // Remove the last comma
            if (sb.Length > 2)
                sb.Length -= 3; // Remove the last comma and newline
            sb.AppendLine();

            sb.Append((new string(' ', (level - 1) * 2)) + "}");

            return sb.ToString();
        }
    }
}
